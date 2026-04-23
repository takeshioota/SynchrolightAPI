using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace SynchrolightAPI.Wpf.Services;

/// <summary>BLE ID読み書きサービス (WinRT API)</summary>
public sealed class BleIdService : IDisposable
{
    private static readonly Guid ServiceUuid = Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb");
    private static readonly Guid WriteCharUuid = Guid.Parse("0000fff5-0000-1000-8000-00805f9b34fb");
    private static readonly Guid NotifyCharUuid = Guid.Parse("0000fff4-0000-1000-8000-00805f9b34fb");

    /// <summary>1デバイス操作あたりの最大時間</summary>
    private static readonly TimeSpan PerDeviceTimeout = TimeSpan.FromSeconds(15);

    public record ScannedDevice(ulong Address, string Name, short Rssi);
    public record DeviceId(int Session, int Row, int Col);

    // ── スキャン ──
    public async Task<List<ScannedDevice>> ScanAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<ulong, ScannedDevice>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            var name = args.Advertisement.LocalName ?? "";
            if (name.Contains("BLELight"))
            {
                found[args.BluetoothAddress] = new ScannedDevice(
                    args.BluetoothAddress, name, args.RawSignalStrengthInDBm);
            }
        };

        watcher.Start();
        try { await Task.Delay(timeout, ct); }
        catch (OperationCanceledException) { }
        finally { watcher.Stop(); }

        return found.Values.OrderByDescending(d => d.Rssi).ToList();
    }

    // ── ID読み取り (FB E2) ──
    public async Task<DeviceId?> ReadIdAsync(ulong address, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PerDeviceTimeout);
        var token = cts.Token;

        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        GattCharacteristic? notify = null;
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? handler = null;

        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token);
            if (device == null) return null;

            (var write, notify, service) = await GetCharacteristicsAsync(device, token);
            if (write == null || notify == null) return null;

            DeviceId? result = null;
            var tcs = new TaskCompletionSource<bool>();

            var cccd = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token);
            if (cccd != GattCommunicationStatus.Success) return null;

            handler = (_, args) =>
            {
                var buf = new byte[args.CharacteristicValue.Length];
                DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(buf);
                if (buf.Length >= 8 && buf[0] == 0xFC && buf[1] == 0xE2)
                {
                    result = new DeviceId(buf[3], (buf[4] << 8) | buf[5], (buf[6] << 8) | buf[7]);
                    tcs.TrySetResult(true);
                }
            };
            notify.ValueChanged += handler;

            await WriteBytes(write, BuildQueryCmd(), token);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            waitCts.CancelAfter(TimeSpan.FromSeconds(3));
            try { await tcs.Task.WaitAsync(waitCts.Token); }
            catch (OperationCanceledException) { }

            return result;
        }
        finally
        {
            if (handler is not null && notify is not null)
                notify.ValueChanged -= handler;
            service?.Dispose();
            device?.Dispose();
            await Task.Delay(200, CancellationToken.None);
        }
    }

    // ── ID書き込み (FB E1) + 照会確認 ──
    public async Task<(bool WriteOk, DeviceId? ReadBack)> WriteIdAsync(
        ulong address, int session, int row, int col, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PerDeviceTimeout);
        var token = cts.Token;

        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        GattCharacteristic? notify = null;
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? handler = null;

        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token);
            if (device == null) return (false, null);

            (var write, notify, service) = await GetCharacteristicsAsync(device, token);
            if (write == null || notify == null) return (false, null);

            bool writeOk = false;
            DeviceId? readBack = null;
            var writeTcs = new TaskCompletionSource<bool>();
            var readTcs = new TaskCompletionSource<bool>();

            var cccd = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token);
            if (cccd != GattCommunicationStatus.Success) return (false, null);

            handler = (_, args) =>
            {
                var buf = new byte[args.CharacteristicValue.Length];
                DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(buf);

                if (buf.Length >= 3 && buf[0] == 0xFC && buf[1] == 0xE1)
                {
                    writeOk = buf[2] == 0x01;
                    writeTcs.TrySetResult(true);
                }
                if (buf.Length >= 8 && buf[0] == 0xFC && buf[1] == 0xE2)
                {
                    readBack = new DeviceId(buf[3], (buf[4] << 8) | buf[5], (buf[6] << 8) | buf[7]);
                    readTcs.TrySetResult(true);
                }
            };
            notify.ValueChanged += handler;

            // 書き込み
            await WriteBytes(write, BuildWriteCmd(session, row, col), token);

            using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                waitCts.CancelAfter(TimeSpan.FromSeconds(3));
                try { await writeTcs.Task.WaitAsync(waitCts.Token); }
                catch (OperationCanceledException) { }
            }

            // 照会確認
            await WriteBytes(write, BuildQueryCmd(), token);

            using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                waitCts.CancelAfter(TimeSpan.FromSeconds(3));
                try { await readTcs.Task.WaitAsync(waitCts.Token); }
                catch (OperationCanceledException) { }
            }

            // 成功なら青点滅
            if (writeOk)
            {
                await WriteBytes(write, BuildBlinkCmd(0, 0, 255), token);
            }

            return (writeOk, readBack);
        }
        finally
        {
            if (handler is not null && notify is not null)
                notify.ValueChanged -= handler;
            service?.Dispose();
            device?.Dispose();
            await Task.Delay(500, CancellationToken.None);
        }
    }

    // ── 赤色点灯 ──
    public async Task<bool> LightRedAsync(ulong address, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PerDeviceTimeout);
        var token = cts.Token;

        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;

        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token);
            if (device == null) return false;

            (var write, _, service) = await GetCharacteristicsAsync(device, token);
            if (write == null) return false;

            await WriteBytes(write, BuildRgbCmd(255, 0, 0), token);
            return true;
        }
        finally
        {
            service?.Dispose();
            device?.Dispose();
            await Task.Delay(200, CancellationToken.None);
        }
    }

    // ── プロトコル ──
    private static byte CalcChecksum(byte[] data) => (byte)(data.Sum(b => b) & 0xFF);

    private static byte[] BuildQueryCmd()
    {
        byte[] cmd = [0xFB, 0xE2];
        return [.. cmd, CalcChecksum(cmd)];
    }

    private static byte[] BuildWriteCmd(int session, int row, int col)
    {
        byte[] cmd =
        [
            0xFB, 0xE1, 0x05,
            (byte)(session & 0xFF),
            (byte)((row >> 8) & 0xFF), (byte)(row & 0xFF),
            (byte)((col >> 8) & 0xFF), (byte)(col & 0xFF),
        ];
        return [.. cmd, CalcChecksum(cmd)];
    }

    private static byte[] BuildRgbCmd(byte r, byte g, byte b, byte power = 1)
    {
        byte[] cmd = [0xFB, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, power, r, g, b, 0x00, 0x00];
        return [.. cmd, CalcChecksum(cmd)];
    }

    private static byte[] BuildBlinkCmd(byte r, byte g, byte b)
    {
        byte[] cmd = [0xFB, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, r, g, b, 0x00, 0x01, 0xF4];
        return [.. cmd, CalcChecksum(cmd)];
    }

    // ── ヘルパ ──
    private static async Task<(GattCharacteristic? Write, GattCharacteristic? Notify, GattDeviceService? Service)>
        GetCharacteristicsAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        var svcResult = await device.GetGattServicesForUuidAsync(
            ServiceUuid, BluetoothCacheMode.Uncached).AsTask(ct);
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            return (null, null, null);

        var service = svcResult.Services[0];

        var wResult = await service.GetCharacteristicsForUuidAsync(WriteCharUuid).AsTask(ct);
        var nResult = await service.GetCharacteristicsForUuidAsync(NotifyCharUuid).AsTask(ct);

        var w = wResult.Status == GattCommunicationStatus.Success && wResult.Characteristics.Count > 0
            ? wResult.Characteristics[0] : null;
        var n = nResult.Status == GattCommunicationStatus.Success && nResult.Characteristics.Count > 0
            ? nResult.Characteristics[0] : null;

        return (w, n, service);
    }

    private static async Task WriteBytes(GattCharacteristic characteristic, byte[] data, CancellationToken ct)
    {
        var writer = new DataWriter();
        writer.WriteBytes(data);
        await characteristic.WriteValueAsync(writer.DetachBuffer()).AsTask(ct);
    }

    public void Dispose() { }
}
