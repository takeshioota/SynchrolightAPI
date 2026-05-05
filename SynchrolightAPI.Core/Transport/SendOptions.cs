namespace SynchrolightAPI.Transport;

/// <summary>
/// 送信オプション: 優先度・ゾーン指定・デッドライン
/// </summary>
public record SendOptions(
    bool HighPriority = false,
    string? TargetZoneId = null,
    DateTimeOffset? Deadline = null,
    /// <summary>
    /// コマンド単位の再送回数オーバーライド。
    /// null=グローバル設定に従う, 1=再送なし(1回のみ送信)
    /// </summary>
    int? RetransmitCount = null
)
{
    public static readonly SendOptions Default = new();
}
