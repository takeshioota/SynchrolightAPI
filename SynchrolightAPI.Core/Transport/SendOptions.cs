namespace SynchrolightAPI.Transport;

/// <summary>
/// 送信オプション: 優先度・ゾーン指定・デッドライン
/// </summary>
public record SendOptions(
    bool HighPriority = false,
    string? TargetZoneId = null,
    DateTimeOffset? Deadline = null
)
{
    public static readonly SendOptions Default = new();
}
