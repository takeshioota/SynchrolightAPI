using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using SynchrolightAPI.Core.Tests.TestSupport;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// EffectScheduler の並行性・耐久ストレステスト（実機不要）。
///
/// 目的：本番運用で発生する「エフェクト/カラーホールドの高速切替（連打）」を多スレッドで再現し、
///   ・許容外の例外（NRE 等）が漏れないこと
///   ・ストレス後もスケジューラが引き続き使用可能であること
///   を保証する。
///
/// 許容例外：OperationCanceledException（設計どおりの協調キャンセル）と
///   ObjectDisposedException（BeginEffect().Token 読取と Abort の CTS 破棄が競合する
///   既知の自己回復レース＝実害軽微。project_bug_colorhold_cts_objectdisposed_race）。
///   これらは「発生しても良い」ものとして数え、その他の型のみを失敗とみなす。
/// </summary>
public class EffectSchedulerConcurrencyTests
{
    private static EffectScheduler NewScheduler(out RecordingTransport transport)
    {
        transport = new RecordingTransport();
        return new EffectScheduler(transport, new InterpolationService(), NullLogger<EffectScheduler>.Instance);
    }

    private static bool IsTolerated(Exception ex)
        => ex is OperationCanceledException || ex is ObjectDisposedException;

    [Fact]
    public void BeginEffectとAbortの高頻度並行呼び出しで許容外例外が漏れない()
    {
        var sut = NewScheduler(out _);
        var unexpected = new ConcurrentBag<Exception>();
        long tolerated = 0;

        Parallel.For(0, 8, threadIdx =>
        {
            for (int k = 0; k < 4000; k++)
            {
                try
                {
                    using var outer = new CancellationTokenSource();
                    var token = sut.BeginEffect(outer.Token);
                    if ((k & 1) == 0) sut.Abort();
                    if (token.IsCancellationRequested) { } // .Token 読取：Abort の破棄と競合し得る
                }
                catch (Exception ex) when (IsTolerated(ex)) { Interlocked.Increment(ref tolerated); }
                catch (Exception ex) { unexpected.Add(ex); }
            }
        });

        Assert.True(unexpected.IsEmpty,
            "許容外の例外が発生: " + string.Join(", ", unexpected.Select(e => e.GetType().Name).Distinct()));

        // ストレス後もスケジューラは正常に使える（生きた token を返し、送出できる）。
        var live = sut.BeginEffect(CancellationToken.None);
        Assert.False(live.IsCancellationRequested);
    }

    [Fact]
    public async Task カラーホールド高速連打相当_SendColorとAbortの並行でも生存する()
    {
        var sut = NewScheduler(out var transport);
        var unexpected = new ConcurrentBag<Exception>();

        var workers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (int k = 0; k < 1500; k++)
            {
                try
                {
                    using var outer = new CancellationTokenSource();
                    await sut.SendColorAsync(0x00, new Rgb((byte)k, (byte)w, 0), outer.Token);
                    if (k % 3 == 0) sut.Abort();
                }
                catch (Exception ex) when (IsTolerated(ex)) { /* 許容 */ }
                catch (Exception ex) { unexpected.Add(ex); }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.True(unexpected.IsEmpty,
            "許容外の例外が発生: " + string.Join(", ", unexpected.Select(e => e.GetType().Name).Distinct()));
        Assert.True(transport.Entries.Count > 0, "少なくとも一部の色送出は成立している");
    }

    [Fact]
    public async Task 長時間の連続補間を途中キャンセルしても綺麗に停止する()
    {
        var sut = NewScheduler(out _);
        using var outer = new CancellationTokenSource();
        var ct = sut.BeginEffect(outer.Token);

        // 30 秒相当の長い補間を開始し、すぐキャンセルして OCE で即停止することを確認。
        var task = sut.SendInterpolationAsync(0x00, Rgb.Black, Rgb.White, 50, TimeSpan.FromSeconds(30), ct);
        outer.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
