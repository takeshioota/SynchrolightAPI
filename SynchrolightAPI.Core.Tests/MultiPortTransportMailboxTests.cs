using Microsoft.Extensions.Logging.Abstractions;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// BUG-20260928-01（No.35 Effect FadeIn/FadeOut/Breathing で演出が止まる）の②対策＝
/// 連続エフェクトの補間フレームを「最新値」チャネル（容量わずか・DropOldest）へ流す挙動の回帰ガード。
/// 旧 must-send（通常キュー FullMode=Wait）では過負荷時に滞留が累積して数秒遅れ＝演出停止になっていた。
/// 最新値方式では滞留が容量上限で頭打ちになり、常に最新へ追従する（凍結しない）。
/// </summary>
public class MultiPortTransportMailboxTests
{
    private static MultiPortTransport NewTransport() =>
        new(NullLogger<MultiPortTransport>.Instance);

    [Fact]
    public async Task LatestOnlyは連続チャネルへ_通常と高優先から分離される_BUG20260928_01()
    {
        var t = NewTransport();

        await t.EnqueueAsync(new byte[] { 1 }, new SendOptions(LatestOnly: true));
        await t.EnqueueAsync(new byte[] { 2 }, SendOptions.Default);
        await t.EnqueueAsync(new byte[] { 3 }, new SendOptions(HighPriority: true));

        Assert.Equal(1, t.ContinuousReader.Count);
        Assert.Equal(1, t.NormalReader.Count);
        Assert.Equal(1, t.HighPriorityReader.Count);

        Assert.True(t.ContinuousReader.TryRead(out var cont));
        Assert.Equal(1, cont!.Packet[0]);
        Assert.True(t.NormalReader.TryRead(out var norm));
        Assert.Equal(2, norm!.Packet[0]);
        Assert.True(t.HighPriorityReader.TryRead(out var high));
        Assert.Equal(3, high!.Packet[0]);
    }

    [Fact]
    public async Task 連続チャネルは過負荷で最古を捨て最新を保持する_滞留が上限で頭打ち_BUG20260928_01()
    {
        var t = NewTransport();

        // 消化されないまま大量に投入（＝過負荷）。must-send（通常キュー）なら全件積まれて滞留が累積し
        // 数秒遅れ＝演出停止になるが、最新値チャネルは容量上限で頭打ちになり最新だけを保持する。
        const int flood = 10;
        for (int i = 1; i <= flood; i++)
            await t.EnqueueAsync(new byte[] { (byte)i }, new SendOptions(LatestOnly: true));

        // 滞留は容量上限を超えない（＝遅延が累積して「演出が止まる」ことがない）。
        Assert.True(t.ContinuousReader.Count <= 8,
            $"連続チャネルの滞留は上限で頭打ちになる（must-send なら {flood} 件積まれる）: count={t.ContinuousReader.Count}");
        Assert.True(t.ContinuousReader.Count < flood,
            "過負荷時は最古フレームが捨てられ全件は積まれない");

        // 残っているのは最新側（最古は捨てられている＝latest-wins）。
        var remaining = new List<int>();
        while (t.ContinuousReader.TryRead(out var e)) remaining.Add(e!.Packet[0]);

        Assert.NotEmpty(remaining);
        Assert.Contains(flood, remaining);                       // 最新(10)は必ず残る
        Assert.DoesNotContain(1, remaining);                     // 最古(1)は捨てられている
        Assert.Equal(remaining.OrderBy(x => x).ToList(), remaining); // FIFO＝投入順で最古側から落ちる
    }

    [Fact]
    public async Task FlushQueueは連続チャネルも破棄する_BUG20260928_01()
    {
        var t = NewTransport();
        await t.EnqueueAsync(new byte[] { 1 }, new SendOptions(LatestOnly: true));
        await t.EnqueueAsync(new byte[] { 2 }, SendOptions.Default);
        await t.EnqueueAsync(new byte[] { 3 }, new SendOptions(HighPriority: true));

        t.FlushQueue();

        Assert.Equal(0, t.ContinuousReader.Count);
        Assert.Equal(0, t.NormalReader.Count);
        Assert.Equal(0, t.HighPriorityReader.Count);
    }
}
