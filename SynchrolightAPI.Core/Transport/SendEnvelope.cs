using System.Diagnostics;

namespace SynchrolightAPI.Transport;

/// <summary>
/// 送信パケットの封筒: ルーティング情報・優先度・タイムスタンプを付与
/// </summary>
public record SendEnvelope(
    byte[] Packet,
    SendOptions Options,
    IReadOnlyList<string>? TargetPortNames,
    long EnqueuedAtTicks,
    string? OperationId = null
)
{
    /// <summary>エンキューからの経過時間を取得</summary>
    public double GetElapsedMs() => Stopwatch.GetElapsedTime(EnqueuedAtTicks).TotalMilliseconds;

    /// <summary>Deadline超過かどうかを判定</summary>
    public bool IsExpired => Options.Deadline.HasValue && DateTimeOffset.UtcNow > Options.Deadline.Value;
}
