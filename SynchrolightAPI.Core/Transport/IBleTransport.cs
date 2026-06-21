namespace SynchrolightAPI.Transport;

/// <summary>
/// BLE（Bluetooth Low Energy）通信の抽象化インターフェース。
/// デバイスのスキャン、接続、データ送受信を提供する。
/// </summary>
public interface IBleTransport
{
    /// <summary>BLELightデバイスをスキャンする。</summary>
    /// <param name="timeoutSeconds">スキャンタイムアウト（秒）</param>
    /// <returns>検出されたデバイス一覧</returns>
    Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(int timeoutSeconds = 5, CancellationToken ct = default);

    /// <summary>指定デバイスに接続する。</summary>
    Task ConnectAsync(string deviceAddress, CancellationToken ct = default);

    /// <summary>接続中のデバイスから切断する。</summary>
    Task DisconnectAsync();

    /// <summary>接続中かどうか。</summary>
    bool IsConnected { get; }

    /// <summary>接続中のデバイスアドレス。</summary>
    string? ConnectedDeviceAddress { get; }

    /// <summary>FFF5特性にデータを書き込む。</summary>
    Task WriteAsync(byte[] data, CancellationToken ct = default);

    /// <summary>
    /// FFF5特性にデータを書き込み、FFF4特性からの応答を待つ。
    /// </summary>
    /// <param name="data">送信データ</param>
    /// <param name="timeoutMs">応答タイムアウト（ms）</param>
    /// <returns>応答データ（タイムアウト時は null）</returns>
    Task<byte[]?> WriteAndWaitResponseAsync(byte[] data, int timeoutMs = 5000, CancellationToken ct = default);
}

/// <summary>BLEデバイス情報</summary>
public record BleDeviceInfo(string Address, string Name, int Rssi);
