using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Wpf.Services;
using SynchrolightAPI.Wpf.ViewModels;

namespace SynchrolightAPI.Wpf;

public partial class App : Application
{
    private IHost _host = null!;
    private SettingsService? _settingsService;
    private CancellationTokenSource? _sseLogCts;
    private Process? _apiProcess;
    private string _apiBaseUrl = "http://localhost:5100";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((ctx, services) =>
            {
                // 設定永続化
                services.AddSingleton<SettingsService>();

                // API Client（全操作を API 経由で実行）
                _apiBaseUrl = ctx.Configuration.GetValue<string>("Api:BaseUrl") ?? "http://localhost:5100";
                services.AddSingleton<HttpClient>(_ => new HttpClient { BaseAddress = new Uri(_apiBaseUrl) });
                services.AddSingleton<SynchrolightApiClient>();

                // BLE
                services.AddSingleton<BleIdService>();

                // ViewModels
                services.AddSingleton<SendLogViewModel>();
                services.AddSingleton<ConnectionViewModel>(sp =>
                    new ConnectionViewModel(
                        sp.GetRequiredService<SynchrolightApiClient>(),
                        sp.GetRequiredService<SettingsService>()));
                services.AddSingleton<TransmitterSettingsViewModel>(sp =>
                    new TransmitterSettingsViewModel(
                        sp.GetRequiredService<SynchrolightApiClient>(),
                        sp.GetRequiredService<SettingsService>()));
                services.AddSingleton<CommandPanelViewModel>(sp =>
                    new CommandPanelViewModel(
                        sp.GetRequiredService<SynchrolightApiClient>(),
                        sp.GetRequiredService<TransmitterSettingsViewModel>(),
                        sp.GetRequiredService<SettingsService>()));
                services.AddSingleton<BleIdPanelViewModel>();
                services.AddSingleton<ZoneSettingsViewModel>(sp =>
                    new ZoneSettingsViewModel(
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<CommandPanelViewModel>()));
                services.AddSingleton<SequencePanelViewModel>();
                services.AddSingleton<MainViewModel>();
            })
            .Build();

        _settingsService = _host.Services.GetRequiredService<SettingsService>();

        await _host.StartAsync();

        // API起動確認 → 未起動なら起動して待機
        await EnsureApiRunningAsync();

        var mainVm = _host.Services.GetRequiredService<MainViewModel>();

        // シーケンスタブを初期選択（タブインデックス 3）
        mainVm.SelectedTabIndex = 3;

        var mainWindow = new Views.MainWindow { DataContext = mainVm };
        mainWindow.Show();
        MainWindow = mainWindow;

        // SSE送信ログストリームをバックグラウンドで購読
        StartSseLogStream();

        // COMポート全接続 → 初期設定コマンド送信
        _ = AutoConnectAndInitAsync();
    }

    /// <summary>
    /// APIが起動しているか確認し、未起動なら起動して応答を待つ
    /// </summary>
    private async Task EnsureApiRunningAsync()
    {
        var httpClient = _host.Services.GetRequiredService<HttpClient>();

        // まず起動済みか確認
        if (await IsApiReadyAsync(httpClient))
            return;

        // API実行ファイルを探す
        var apiExePath = FindApiExe();
        if (apiExePath == null)
        {
            MessageBox.Show(
                "APIの実行ファイルが見つかりません。\npublish/Api/SynchrolightAPI.Api.exe を確認してください。",
                "API起動エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // APIプロセスを起動
        _apiProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = apiExePath,
                WorkingDirectory = Path.GetDirectoryName(apiExePath),
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        _apiProcess.Start();

        // APIが応答するまで待機（最大15秒）
        var ready = false;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            if (await IsApiReadyAsync(httpClient))
            {
                ready = true;
                break;
            }
        }

        if (!ready)
        {
            MessageBox.Show(
                "APIの起動がタイムアウトしました。",
                "API起動エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static async Task<bool> IsApiReadyAsync(HttpClient httpClient)
    {
        try
        {
            var resp = await httpClient.GetAsync("api/transport/status");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string? FindApiExe()
    {
        // WPFの実行ディレクトリから相対的にpublish/Apiを探す
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            // publishフォルダ（プロジェクトルート直下）
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "publish", "Api", "SynchrolightAPI.Api.exe")),
            // 開発時: プロジェクトルート/publish/Api
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "publish", "Api", "SynchrolightAPI.Api.exe")),
            // 同階層
            Path.Combine(baseDir, "SynchrolightAPI.Api.exe"),
            // 隣のpublishフォルダ
            Path.GetFullPath(Path.Combine(baseDir, "..", "Api", "SynchrolightAPI.Api.exe")),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// COMポート全選択→接続→初期設定コマンド送信
    /// </summary>
    private async Task AutoConnectAndInitAsync()
    {
        var connectionVm = _host.Services.GetRequiredService<ConnectionViewModel>();
        var transmitterVm = _host.Services.GetRequiredService<TransmitterSettingsViewModel>();
        var apiClient = _host.Services.GetRequiredService<SynchrolightApiClient>();

        // COMポートスキャン→全選択→接続
        var connected = await connectionVm.ScanAndConnectAllAsync();
        if (!connected) return;

        // 初期設定コマンド送信（チャンネル・出力の設定）
        try
        {
            await apiClient.InitTransmitterAsync(
                transmitterVm.SelectedChannel,
                transmitterVm.SelectedPower);
        }
        catch
        {
            // 初期設定失敗は無視（接続は成功している）
        }
    }

    private void StartSseLogStream()
    {
        _sseLogCts = new CancellationTokenSource();
        var apiClient = _host.Services.GetRequiredService<SynchrolightApiClient>();
        var sendLogVm = _host.Services.GetRequiredService<SendLogViewModel>();
        var ct = _sseLogCts.Token;

        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await apiClient.StreamSendLogAsync(entry =>
                    {
                        sendLogVm.AddEntry(entry.Direction, entry.Hex);
                    }, ct);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // API未起動等で接続失敗 → 3秒後にリトライ
                    try { await Task.Delay(3000, ct); } catch { break; }
                }
            }
        }, ct);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // SSEストリーム停止
        _sseLogCts?.Cancel();
        _sseLogCts?.Dispose();

        // 終了時に設定を保存
        _settingsService?.Save();

        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
