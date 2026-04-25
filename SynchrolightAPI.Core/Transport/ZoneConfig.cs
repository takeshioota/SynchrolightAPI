namespace SynchrolightAPI.Transport;

/// <summary>
/// ゾーン定義: 論理ゾーンを物理COMポート・TXチャネル・TX電力にマッピング
/// </summary>
public record ZoneConfig(
    string ZoneId,
    string PortName,
    byte TxChannel,
    byte TxPower
);
