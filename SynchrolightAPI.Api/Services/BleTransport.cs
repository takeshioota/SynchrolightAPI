using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Services;

/// <summary>
/// WinRT (Windows.Devices.Bluetooth) を使用した BLE 通信実装。
/// BLELight デバイスのスキャン、接続、FFF5 特性への書き込み、FFF4 特性からの通知受信を提供する。
/// </summary>
public class BleTransport : IBleTransport, IDisposable
{
    private static readonly Guid ServiceGuid = Guid.Parse(BleProtocol.ServiceUuid);
    private static readonly Guid WriteCharGuid = Guid.Parse(BleProtocol.WriteCharUuid);
    private static readonly Guid NotifyCharGuid = Guid.Parse(BleProtocol.NotifyCharUuid);

    private readonly ILogger<BleTransport> _logger;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private TaskCompletionSource<byte[]>? _responseTcs;
    private bool _disposed;

    public BleTransport(ILogger<BleTransport> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _device != null && _writeChar != null;
    public string? ConnectedDeviceAddress => _device?.BluetoothAddress.ToString("X12");

    public async Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(int timeoutSeconds = 5, CancellationToken ct = default)
    {
        _logger.LogInformation("BLE スキャン開始（{Timeout}秒）", timeoutSeconds);

        var devices = new Dictionary<ulong, BleDeviceInfo>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        var scanComplete = new TaskCompletionSource<bool>();

        watcher.Received += (sender, args) =>
        {
            var name = args.Advertisement.LocalName;
            if (!string.IsNullOrEmpty(name) && name.Contains(BleProtocol.DefaultDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                var addr = args.BluetoothAddress.ToString("X12");
                var formatted = string.Join(":", Enumerable.Range(0, 6).Select(i => addr.Substring(i * 2, 2)));
                devices[args.BluetoothAddress] = new BleDeviceInfo(formatted, name, (int)args.RawSignalStrengthInDBm);
            }
        };

        watcher.Start();

        try
        {
            await Task.Delay(timeoutSeconds * 1000, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            watcher.Stop();
        }

        _logger.LogInformation("BLE スキャン完了: {Count} デバイス検出", devices.Count);
        return devices.Values.ToList();
    }

    public async Task ConnectAsync(string deviceAddress, CancellationToken ct = default)
    {
        // アドレス文字列を ulong に変換（"AA:BB:CC:DD:EE:FF" → 数値）
        var addrStr = deviceAddress.Replace(":", "").Replace("-", "");
        if (!ulong.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out var btAddr))
            throw new ArgumentException($"Invalid BLE address: {deviceAddress}");

        _logger.LogInformation("BLE 接続中: {Address}", deviceAddress);

        // デバイス取得
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(btAddr);
        if (_device == null)
            throw new IOException($"BLE デバイスが見つかりません: {deviceAddress}");

        // GATT サービス取得
        var servicesResult = await _device.GetGattServicesForUuidAsync(ServiceGuid);
        if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            throw new IOException($"GATT サービス FFF0 が見つかりません (status={servicesResult.Status})");

        var service = servicesResult.Services[0];

        // 書き込み特性（FFF5）取得
        var writeResult = await service.GetCharacteristicsForUuidAsync(WriteCharGuid);
        if (writeResult.Status != GattCommunicationStatus.Success || writeResult.Characteristics.Count == 0)
            throw new IOException("書き込み特性 FFF5 が見つかりません");
        _writeChar = writeResult.Characteristics[0];

        // 通知特性（FFF4）取得
        var notifyResult = await service.GetCharacteristicsForUuidAsync(NotifyCharGuid);
        if (notifyResult.Status != GattCommunicationStatus.Success || notifyResult.Characteristics.Count == 0)
            throw new IOException("通知特性 FFF4 が見つかりません");
        _notifyChar = notifyResult.Characteristics[0];

        // 通知を有効化
        var cccdResult = await _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccdResult != GattCommunicationStatus.Success)
            throw new IOException($"通知の有効化に失敗 (status={cccdResult})");

        _notifyChar.ValueChanged += OnNotifyValueChanged;

        _logger.LogInformation("BLE 接続完了: {Address}, デバイス名={Name}",
            deviceAddress, _device.Name);
    }

    public async Task DisconnectAsync()
    {
        if (_notifyChar != null)
        {
            _notifyChar.ValueChanged -= OnNotifyValueChanged;
            try
            {
                await _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { /* ignore */ }
        }

        _writeChar = null;
        _notifyChar = null;
        _device?.Dispose();
        _device = null;

        _logger.LogInformation("BLE 切断完了");
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        if (_writeChar == null)
            throw new InvalidOperationException("BLE デバイスに接続されていません");

        var buffer = data.AsBuffer();
        var result = await _writeChar.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
        if (result != GattCommunicationStatus.Success)
            throw new IOException($"BLE 書き込み失敗 (status={result})");
    }

    public async Task<byte[]?> WriteAndWaitResponseAsync(byte[] data, int timeoutMs = 5000, CancellationToken ct = default)
    {
        if (_writeChar == null)
            throw new InvalidOperationException("BLE デバイスに接続されていません");

        _responseTcs = new TaskCompletionSource<byte[]>();

        // 書き込み
        var buffer = data.AsBuffer();
        var result = await _writeChar.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
        if (result != GattCommunicationStatus.Success)
        {
            _responseTcs = null;
            throw new IOException($"BLE 書き込み失敗 (status={result})");
        }

        // 応答待ち（タイムアウト付き）
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            var response = await _responseTcs.Task.WaitAsync(cts.Token);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("BLE 応答タイムアウト ({Timeout}ms)", timeoutMs);
            return null;
        }
        finally
        {
            _responseTcs = null;
        }
    }

    private void OnNotifyValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        _logger.LogDebug("BLE 通知受信: [{Hex}]", BitConverter.ToString(data));

        _responseTcs?.TrySetResult(data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
