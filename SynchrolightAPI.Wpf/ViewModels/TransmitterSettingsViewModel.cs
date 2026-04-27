using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Diagnostics;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class TransmitterSettingsViewModel : ObservableObject
{
    private readonly LightingService _lighting;
    private readonly ITransport _transport;
    private readonly SettingsService _settings;
    private readonly LatencyTracker? _latencyTracker;
    private readonly DispatcherTimer _statusTimer;
    private DispatcherTimer? _keepAliveTimer;
    private byte[] _lastSentPacket = LightProtocol.BuildA2_GlobalColor(0x01, 0, 0, 0);

    public int[] ChannelOptions { get; } = [1, 2, 3, 4];
    public int[] PowerOptions { get; } = [0, 1, 2, 3];
    public int[] KeepAliveIntervalOptions { get; } = [60, 120, 180, 300, 480];

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
    private int _selectedKeepAliveInterval = 120;

    [ObservableProperty]
    private string _keepAliveStatus = "停止中";

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
        _keepAliveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(SelectedKeepAliveInterval)
        };
        _keepAliveTimer.Tick += async (_, _) => await SendKeepAliveAsync();
        _keepAliveTimer.Start();
        KeepAliveStatus = $"動作中 ({SelectedKeepAliveInterval}秒間隔)";
    }

    private void StopKeepAlive()
    {
        _keepAliveTimer?.Stop();
        _keepAliveTimer = null;
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

    partial void OnSelectedKeepAliveIntervalChanged(int value)
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
