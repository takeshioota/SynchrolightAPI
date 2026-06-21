using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Services;

/// <summary>
/// BLE通信のスタブ実装。
/// 実機接続前の開発用。将来的にWinRT（Windows.Devices.Bluetooth）で置き換える。
/// </summary>
public class BleTransportStub : IBleTransport
{
    private readonly ILogger<BleTransportStub> _logger;
    private bool _connected;
    private string? _connectedAddress;

    public BleTransportStub(ILogger<BleTransportStub> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _connected;
    public string? ConnectedDeviceAddress => _connectedAddress;

    public Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(int timeoutSeconds = 5, CancellationToken ct = default)
    {
        _logger.LogInformation("[BLE Stub] スキャン実行（{Timeout}秒）", timeoutSeconds);

        // スタブ: ダミーデバイスを返す
        var devices = new List<BleDeviceInfo>
        {
            new("00:00:00:00:00:01", "BLELight-001", -50),
            new("00:00:00:00:00:02", "BLELight-002", -60),
        };
        return Task.FromResult<IReadOnlyList<BleDeviceInfo>>(devices);
    }

    public Task ConnectAsync(string deviceAddress, CancellationToken ct = default)
    {
        _logger.LogInformation("[BLE Stub] 接続: {Address}", deviceAddress);
        _connected = true;
        _connectedAddress = deviceAddress;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _logger.LogInformation("[BLE Stub] 切断: {Address}", _connectedAddress);
        _connected = false;
        _connectedAddress = null;
        return Task.CompletedTask;
    }

    public Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        _logger.LogDebug("[BLE Stub] Write: {Len}bytes [{Hex}]",
            data.Length, BitConverter.ToString(data, 0, Math.Min(data.Length, 8)));
        return Task.CompletedTask;
    }

    public Task<byte[]?> WriteAndWaitResponseAsync(byte[] data, int timeoutMs = 5000, CancellationToken ct = default)
    {
        _logger.LogDebug("[BLE Stub] WriteAndWait: {Len}bytes [{Hex}]",
            data.Length, BitConverter.ToString(data, 0, Math.Min(data.Length, 8)));

        // スタブ: 成功応答を返す
        byte cmdByte = data.Length >= 2 ? data[1] : (byte)0x00;
        var response = new byte[] { 0xFC, cmdByte, 0x01, 0xFF };
        return Task.FromResult<byte[]?>(response);
    }
}
