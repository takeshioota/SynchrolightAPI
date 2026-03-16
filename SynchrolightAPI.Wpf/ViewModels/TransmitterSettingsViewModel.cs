using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class TransmitterSettingsViewModel : ObservableObject
{
    private readonly LightingService _lighting;
    private readonly ITransport _transport;
    private readonly DispatcherTimer _statusTimer;
    private DispatcherTimer? _keepAliveTimer;
    private byte[] _lastSentPacket = LightProtocol.BuildA2_GlobalColor(0, 0, 0);

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
    private int _connectedPorts;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private bool _isKeepAliveEnabled;

    [ObservableProperty]
    private int _selectedKeepAliveInterval = 120;

    [ObservableProperty]
    private string _keepAliveStatus = "停止中";

    public TransmitterSettingsViewModel(LightingService lighting, ITransport transport)
    {
        _lighting = lighting;
        _transport = transport;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
    }

    [RelayCommand]
    private async Task SetChannelAsync()
    {
        await _transport.EnqueueAsync(
            LightProtocol.BuildTxSetChannel((byte)SelectedChannel));
    }

    [RelayCommand]
    private async Task SetPowerAsync()
    {
        await _transport.EnqueueAsync(
            LightProtocol.BuildTxSetPower((byte)SelectedPower));
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
        if (IsKeepAliveEnabled)
        {
            StartKeepAlive();
        }
    }

    private void RefreshStatus()
    {
        var s = _transport.GetStatus();
        QueueLength = s.QueueLength;
        ConnectedPorts = s.ConnectedPorts;
        LastError = s.LastError;
    }
}
