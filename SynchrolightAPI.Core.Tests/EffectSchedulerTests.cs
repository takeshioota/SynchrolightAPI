using Microsoft.Extensions.Logging.Abstractions;
using SynchrolightAPI.Core.Tests.TestSupport;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// 補間オーケストレーター(EffectScheduler)の挙動テスト。
/// RecordingTransport（実機不要のフェイク）を注入し、送出パケット・優先度・キャンセル・
/// キューフラッシュ・所有権ガード(BUG-20260902-01)を検証する。純ロジック＋非同期（実機不要）。
/// </summary>
public class EffectSchedulerTests
{
    private static EffectScheduler NewScheduler(out RecordingTransport transport)
    {
        transport = new RecordingTransport();
        return new EffectScheduler(transport, new InterpolationService(), NullLogger<EffectScheduler>.Instance);
    }

    // ───────────────────────── BeginEffect / Abort ─────────────────────────

    [Fact]
    public void BeginEffect_新規操作のトークンは非キャンセル()
    {
        var sut = NewScheduler(out _);
        var token = sut.BeginEffect(CancellationToken.None);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void BeginEffect_前操作を自動キャンセルしキューをフラッシュする()
    {
        var sut = NewScheduler(out var transport);
        var t1 = sut.BeginEffect(CancellationToken.None);
        var t2 = sut.BeginEffect(CancellationToken.None);

        Assert.True(t1.IsCancellationRequested, "前操作トークンはキャンセルされる");
        Assert.False(t2.IsCancellationRequested, "新操作トークンは生存");
        Assert.True(transport.FlushCount >= 2, "各 BeginEffect でキューがフラッシュされる");
    }

    [Fact]
    public void BeginEffect_既にキャンセル済みのouterCtは現行操作を保護しno_op()
    {
        // BUG-20260902-01（所有権ガード）: stale 再入（キャンセル済み outerCt）では現行操作を横取りしない。
        var sut = NewScheduler(out var transport);
        var live = sut.BeginEffect(CancellationToken.None);
        int flushAfterFirst = transport.FlushCount;

        using var cancelledOuter = new CancellationTokenSource();
        cancelledOuter.Cancel();
        var stale = sut.BeginEffect(cancelledOuter.Token);

        Assert.True(stale.IsCancellationRequested, "stale 再入には即キャンセル済みトークンを返す");
        Assert.False(live.IsCancellationRequested, "現行操作は保護され生きたまま");
        Assert.Equal(flushAfterFirst, transport.FlushCount); // 追加フラッシュしない
    }

    [Fact]
    public void Abort_アクティブ操作をキャンセルしキューをフラッシュする()
    {
        var sut = NewScheduler(out var transport);
        var token = sut.BeginEffect(CancellationToken.None);
        int before = transport.FlushCount;

        sut.Abort();

        Assert.True(token.IsCancellationRequested);
        Assert.True(transport.FlushCount > before);
    }

    [Fact]
    public void Abort_アクティブ操作が無くてもキューフラッシュのみ実施し例外なし()
    {
        var sut = NewScheduler(out var transport);
        sut.Abort();
        Assert.Equal(1, transport.FlushCount);
    }

    // ───────────────────────── 送出 ─────────────────────────

    [Fact]
    public async Task SendFrameAsync_A2単色を1件送出し直近色を記録する()
    {
        var sut = NewScheduler(out var transport);
        var ct = sut.BeginEffect(CancellationToken.None);

        await sut.SendFrameAsync(0x00, Rgb.Red, ct);

        var last = transport.Last!.Value;
        Assert.Equal(0xA2, last.Packet[0]);
        Assert.Equal((255, 0, 0), (last.Packet[2], last.Packet[3], last.Packet[4]));
        Assert.True(sut.TryGetLastColor(out var field, out var color));
        Assert.Equal(0x00, field);
        Assert.Equal(Rgb.Red, color);
    }

    [Fact]
    public void TryGetLastColor_未送出はfalse()
    {
        var sut = NewScheduler(out _);
        Assert.False(sut.TryGetLastColor(out _, out _));
    }

    [Fact]
    public async Task LatchColorHighPriorityAsync_高優先フラグ付きで送出する()
    {
        var sut = NewScheduler(out var transport);
        var ct = sut.BeginEffect(CancellationToken.None);

        await sut.LatchColorHighPriorityAsync(0x00, Rgb.Green, ct);

        var last = transport.Last!.Value;
        Assert.True(last.Options.HighPriority, "ラッチは高優先キューで送出される");
        Assert.Equal((0, 255, 0), (last.Packet[2], last.Packet[3], last.Packet[4]));
    }

    [Fact]
    public async Task SendInterpolationAsync_端点は開始色と終了色_件数は下限以上()
    {
        var sut = NewScheduler(out var transport);
        var ct = sut.BeginEffect(CancellationToken.None);

        await sut.SendInterpolationAsync(0x00, Rgb.Black, Rgb.White, 5, TimeSpan.FromMilliseconds(300), ct);

        var frames = transport.Entries.Where(e => e.Packet[0] == 0xA2).ToList();
        Assert.True(frames.Count >= 10, $"補間フレーム数は下限(10)以上: {frames.Count}");
        var first = frames[0].Packet;
        var last = frames[^1].Packet;
        Assert.Equal((0, 0, 0), (first[2], first[3], first[4]));
        Assert.Equal((255, 255, 255), (last[2], last[3], last[4]));
    }

    [Fact]
    public async Task SendInterpolationAsync_補間フレームはDeadlineなしのmust_send_光が飛ぶ対策()
    {
        // BUG-20260928-01: 補間の一意フレームは 80ms コアレスで破棄されると「光が飛ぶ」。
        // すべての A2 補間フレームは Deadline=null（IsExpired=false → must-send）で送出されること。
        var sut = NewScheduler(out var transport);
        var ct = sut.BeginEffect(CancellationToken.None);

        await sut.SendInterpolationAsync(0x00, Rgb.Black, Rgb.White, 5, TimeSpan.FromMilliseconds(300), ct,
            curve: FadeCurve.Perceptual);

        var frames = transport.Entries.Where(e => e.Packet[0] == 0xA2).ToList();
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.Null(f.Options.Deadline));
        Assert.All(frames, f => Assert.Equal(1, f.Options.RetransmitCount));
    }

    [Fact]
    public async Task SendFrameForDurationAsync_既定は80msコアレス期限つき_キープアライブ用途()
    {
        // 同色を繰り返す保持/キープアライブは従来どおり Deadline つき（渋滞時は破棄されても無害）。
        var sut = NewScheduler(out var transport);
        var ct = sut.BeginEffect(CancellationToken.None);

        await sut.SendFrameForDurationAsync(0x00, Rgb.Blue, TimeSpan.FromMilliseconds(10), ct);

        var frames = transport.Entries.Where(e => e.Packet[0] == 0xA2).ToList();
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.NotNull(f.Options.Deadline));
    }

    [Fact]
    public async Task SendInterpolationAsync_キャンセル済みトークンで即中断する()
    {
        var sut = NewScheduler(out _);
        using var outer = new CancellationTokenSource();
        var ct = sut.BeginEffect(outer.Token);
        outer.Cancel(); // 親をキャンセル → effectCt も連動キャンセル

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.SendInterpolationAsync(0x00, Rgb.Black, Rgb.White, 20, TimeSpan.FromSeconds(1), ct));
    }

    [Fact]
    public async Task SendColorAsync_前操作を停止し即時にA2を送出する()
    {
        var sut = NewScheduler(out var transport);
        var prev = sut.BeginEffect(CancellationToken.None);

        await sut.SendColorAsync(0x00, Rgb.Blue, CancellationToken.None);

        Assert.True(prev.IsCancellationRequested, "即時送出は前操作を停止する");
        var last = transport.Last!.Value;
        Assert.Equal((0, 0, 255), (last.Packet[2], last.Packet[3], last.Packet[4]));
    }
}
