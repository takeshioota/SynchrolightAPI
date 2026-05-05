using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Diagnostics;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Transport;
using SynchrolightAPI.Wpf.Models;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class TransmitterSettingsViewModel : ObservableObject
{
    private readonly LightingService _lighting;
    private readonly ITransport _transport;
    private readonly SettingsService _settings;
    private readonly LatencyTracker? _latencyTracker;
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
    public int[] RetransmitCountOptions { get; } = [1, 2, 3, 4, 5];
    public int[] RetransmitIntervalOptions { get; } = [5, 7, 10];

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
        LightingService lighting,
        ITransport transport,
        SettingsService settings,
        LatencyTracker? latencyTracker = null)
    {
        _lighting = lighting;
        _transport = transport;
        _settings = settings;
        _latencyTracker = latencyTracker;

        // 保存済み設定を復元
        RestoreSettings();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
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
        await _transport.EnqueueAsync(
            LightProtocol.BuildTxSetChannel((byte)SelectedChannel),
            SendOptions.Default with { HighPriority = true });
    }

    [RelayCommand]
    private async Task SetPowerAsync()
    {
        await _transport.EnqueueAsync(
            LightProtocol.BuildTxSetPower((byte)SelectedPower),
            SendOptions.Default with { HighPriority = true });
    }

    [RelayCommand]
    private async Task InitializeTransmitterAsync()
    {
        await _lighting.InitializeTransmitterAsync(
            (byte)SelectedChannel, (byte)SelectedPower);
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
            var status = _transport.GetStatus();
            if (status.ConnectedPorts > 0)
            {
                await _transport.EnqueueAsync(_lastSentPacket);
            }
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

    private void RefreshStatus()
    {
        var s = _transport.GetStatus();
        QueueLength = s.QueueLength;
        HighPriorityQueueLength = s.HighPriorityQueueLength;
        ConnectedPorts = s.ConnectedPorts;
        DisconnectedPorts = s.DisconnectedPorts;
        LastError = s.LastError;

        // レイテンシ統計更新
        if (_latencyTracker != null)
        {
            var stats = _latencyTracker.GetStatistics(TimeSpan.FromSeconds(30));
            LatencyText = stats.Count > 0
                ? $"P50={stats.P50Ms:F1} P95={stats.P95Ms:F1} ({stats.Count}件)"
                : "-";
        }
    }
}
