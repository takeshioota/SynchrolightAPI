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
    int? RetransmitCount = null,
    /// <summary>
    /// 連続エフェクト（フェード／ブレス等）の補間フレーム用。true のとき専用の「最新値」チャネル
    /// （容量わずか・DropOldest）へ送る。消化が一時的に追いつかない場合でも古い中間フレームを捨てて
    /// 常に最新だけを送るため、滞留が数秒に累積して「演出が止まる」事象（BUG-20260928-01）を防ぐ。
    /// </summary>
    bool LatestOnly = false
)
{
    public static readonly SendOptions Default = new();
}
