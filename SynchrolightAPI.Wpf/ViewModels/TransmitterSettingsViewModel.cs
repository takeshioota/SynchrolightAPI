using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Wpf.Models;
using SynchrolightAPI.Wpf.Services;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class TransmitterSettingsViewModel : ObservableObject
{
    private readonly SynchrolightApiClient _apiClient;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _statusTimer;
    private CancellationTokenSource? _keepAliveCts;
    private byte[] _lastSentPacket = LightProtocol.BuildA2_GlobalColor(0x00, 0, 0, 0);

    public int[] ChannelOptions { get; } = [1, 2, 3, 4];
    public int[] PowerOptions { get; } = [0, 1, 2, 3];
    public IntervalOption[] KeepAliveIntervalOptions { get; } =
    [
        new(0.04, "40ms"),
        new(0.06, "60ms"),
        new(0.08, "80ms"),
        new(0.1,  "100ms"),
        new(0.2,  "200ms"),
        new(0.5,  "500ms"),
        new(1,    "1秒"),
        new(5,    "5秒"),
        new(10,   "10秒"),
        new(30,   "30秒"),
        new(60,   "60秒"),
        new(120,  "120秒"),
        new(180,  "180秒"),
        new(300,  "300秒"),
        new(480,  "480秒"),
    ];
    public IntOption[] RetransmitCountOptions { get; } =
    [
        new(1, "1回"),
        new(2, "2回"),
        new(3, "3回"),
        new(4, "4回"),
        new(5, "5回"),
    ];
    public IntOption[] RetransmitIntervalOptions { get; } =
    [
        new(5,  "5ms"),
        new(7,  "7ms"),
        new(10, "10ms"),
        new(20, "20ms"),
        new(40, "40ms"),
        new(60, "60ms"),
        new(80, "80ms"),
    ];

    [ObservableProperty]
    private int _selectedChannel = 4;

    [ObservableProperty]
    private int _selectedPower;

    [ObservableProperty]
    private int _queueLength;

    [ObservableProperty]
    private int _highPriorityQueueLength;

    [ObservableProperty]
    private int _connectedPorts;

    [ObservableProperty]
    private int _disconnectedPorts;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private bool _isKeepAliveEnabled;

    [ObservableProperty]
    private double _selectedKeepAliveInterval = 120;

    [ObservableProperty]
    private string _keepAliveStatus = "停止中";

    // 再送設定
    [ObservableProperty]
    private int _selectedRetransmitCount = 3;

    [ObservableProperty]
    private int _selectedRetransmitInterval = 5;

    // レイテンシ統計
    [ObservableProperty]
    private string _latencyText = "-";

    public TransmitterSettingsViewModel(
        SynchrolightApiClient apiClient,
        SettingsService settings)
    {
        _apiClient = apiClient;
        _settings = settings;

        // 保存済み設定を復元
        RestoreSettings();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _statusTimer.Start();
    }

    private void RestoreSettings()
    {
        var s = _settings.Current;
        SelectedChannel = s.TxChannel;
        SelectedPower = s.TxPower;
        SelectedKeepAliveInterval = s.KeepAliveIntervalSeconds;
        IsKeepAliveEnabled = s.KeepAliveEnabled;
        SelectedRetransmitCount = s.RetransmitCount;
        SelectedRetransmitInterval = s.RetransmitIntervalMs;
        if (IsKeepAliveEnabled) StartKeepAlive();
    }

    partial void OnSelectedChannelChanged(int value)
    {
        _settings.Update(s => s.TxChannel = value);
    }

    partial void OnSelectedPowerChanged(int value)
    {
        _settings.Update(s => s.TxPower = value);
    }

    partial void OnIsKeepAliveEnabledChanged(bool value)
    {
        _settings.Update(s => s.KeepAliveEnabled = value);
    }

    partial void OnSelectedRetransmitCountChanged(int value)
    {
        _settings.Update(s => s.RetransmitCount = value);
    }

    partial void OnSelectedRetransmitIntervalChanged(int value)
    {
        _settings.Update(s => s.RetransmitIntervalMs = value);
    }

    [RelayCommand]
    private async Task SetChannelAsync()
    {
        await _apiClient.SetChannelAsync(SelectedChannel);
    }

    [RelayCommand]
    private async Task SetPowerAsync()
    {
        await _apiClient.SetPowerAsync(SelectedPower);
    }

    [RelayCommand]
    private async Task InitializeTransmitterAsync()
    {
        await _apiClient.InitTransmitterAsync(SelectedChannel, SelectedPower);
    }

    [RelayCommand]
    private void ToggleKeepAlive()
    {
        if (IsKeepAliveEnabled)
        {
            StartKeepAlive();
        }
        else
        {
            StopKeepAlive();
        }
    }

    /// <summary>最後に送信したパケットを記録（キープアライブ再送用）</summary>
    public void UpdateLastSentPacket(byte[] packet)
    {
        if (packet.Length > 0)
            _lastSentPacket = packet;
    }

    private void StartKeepAlive()
    {
        StopKeepAlive();
        _keepAliveCts = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(SelectedKeepAliveInterval);
        var token = _keepAliveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(interval, token);
                    await SendKeepAliveAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }, token);

        KeepAliveStatus = SelectedKeepAliveInterval >= 1
            ? $"動作中 ({SelectedKeepAliveInterval:G}秒間隔)"
            : $"動作中 ({SelectedKeepAliveInterval * 1000:F0}ms間隔)";
    }

    private void StopKeepAlive()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;
        KeepAliveStatus = "停止中";
    }

    private async Task SendKeepAliveAsync()
    {
        try
        {
            var base64 = Convert.ToBase64String(_lastSentPacket);
            await _apiClient.SendKeepAliveAsync(base64);
        }
        catch
        {
            // キープアライブ送信エラーは無視
        }
    }

    partial void OnSelectedKeepAliveIntervalChanged(double value)
    {
        _settings.Update(s => s.KeepAliveIntervalSeconds = value);
        if (IsKeepAliveEnabled)
        {
            StartKeepAlive();
        }
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var s = await _apiClient.GetTransportStatusAsync();
            QueueLength = s.QueueLength;
            HighPriorityQueueLength = s.HighPriorityQueueLength;
            ConnectedPorts = s.ConnectedPorts;
            DisconnectedPorts = s.DisconnectedPorts;
            LastError = s.LastError;
        }
        catch
        {
            // ステータス取得エラーは無視
        }
    }
}
