namespace SynchrolightAPI.Settings;

/// <summary>
/// ユーザー設定モデル: JSONファイルに永続化される
/// </summary>
public class UserSettings
{
    /// <summary>選択されたCOMポート名</summary>
    public List<string> SelectedPorts { get; set; } = [];

    /// <summary>送信チャネル (1-4)</summary>
    public int TxChannel { get; set; } = 4;

    /// <summary>送信電力 (0-3)</summary>
    public int TxPower { get; set; } = 0;

    /// <summary>最終選択色</summary>
    public RgbSetting LastColor { get; set; } = new(255, 0, 0);

    /// <summary>KeepAlive間隔（秒）</summary>
    public int KeepAliveIntervalSeconds { get; set; } = 120;

    /// <summary>KeepAlive有効</summary>
    public bool KeepAliveEnabled { get; set; }

    /// <summary>ゾーン設定</summary>
    public List<ZoneSettingEntry> Zones { get; set; } = [];

    /// <summary>速度プリセット（名前 → 送信間隔ms）</summary>
    public Dictionary<string, int> SpeedPresets { get; set; } = new()
    {
        ["Speed01"] = 5,
        ["Speed02"] = 10,
        ["Speed03"] = 20,
        ["Speed04"] = 50
    };
}

/// <summary>RGB色設定</summary>
public record RgbSetting(byte R, byte G, byte B);

/// <summary>ゾーン設定エントリ</summary>
public record ZoneSettingEntry(string ZoneId, string PortName, byte TxChannel, byte TxPower);
