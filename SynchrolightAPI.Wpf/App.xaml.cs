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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((ctx, services) =>
            {
                // 設定永続化
                services.AddSingleton<SettingsService>();

                // API Client（全操作を API 経由で実行）
                var apiBaseUrl = ctx.Configuration.GetValue<string>("Api:BaseUrl") ?? "http://localhost:5100";
                services.AddSingleton<HttpClient>(_ => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });
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

        var mainVm = _host.Services.GetRequiredService<MainViewModel>();
        var mainWindow = new Views.MainWindow { DataContext = mainVm };
        mainWindow.Show();
        MainWindow = mainWindow;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
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
