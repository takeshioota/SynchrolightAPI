using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SynchrolightAPI.Core.Tests.TestSupport;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// エフェクトエンジン(EffectEngine)の挙動テスト。
/// 実 EffectScheduler + RecordingTransport（実機不要）で、
///  ・非連続(Continuous=false) 完了後に「保持すべき最終色」を送り続けること
///  ・連続(Continuous=true) はキャンセルまで動き続け、キャンセルで綺麗に終了すること
/// を検証する。見た目の滑らかさ自体は実機確認項目。
/// </summary>
public class EffectEngineTests
{
    private static EffectEngine NewEngine(out RecordingTransport transport)
    {
        transport = new RecordingTransport();
        var scheduler = new EffectScheduler(transport, new InterpolationService(), NullLogger<EffectScheduler>.Instance);
        return new EffectEngine(scheduler, NullLogger<EffectEngine>.Instance);
    }

    private static async Task<bool> WaitForColorAsync(RecordingTransport t, (byte, byte, byte) expected, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (t.LastA2Color() is { } c && c == expected) return true;
            await Task.Delay(15);
        }
        return false;
    }

    // 非連続エフェクトは「完了後に保持する最終色」を送り続ける（GetFinalColor 相当の挙動）。
    [Theory]
    [InlineData(EffectType.FadeIn, 255, 0, 0, 255, 0, 0)]   // 黒→色 → 色(赤)を保持
    [InlineData(EffectType.FadeOut, 255, 0, 0, 0, 0, 0)]    // 色→黒 → 黒を保持
    [InlineData(EffectType.SevenColor, 0, 0, 0, 255, 255, 255)] // 7色 → 白を保持
    public async Task 非連続エフェクトは最終色を保持送信する(
        EffectType type, byte r, byte g, byte b, byte fr, byte fg, byte fb)
    {
        var eng = NewEngine(out var transport);
        using var cts = new CancellationTokenSource();

        var run = eng.RunAsync(
            new EffectParams(type, new Rgb(r, g, b),
                CycleDuration: TimeSpan.FromMilliseconds(40), FadeSteps: 8, Continuous: false),
            cts.Token);

        bool reached = await WaitForColorAsync(transport, (fr, fg, fb));
        cts.Cancel();
        await run; // RunAsync は OCE を内部 catch するため例外は出ない

        Assert.True(reached, $"{type} の最終色 ({fr},{fg},{fb}) が保持送信されること");
    }

    [Fact]
    public async Task 非連続Flashは黒を最終色として保持する()
    {
        var eng = NewEngine(out var transport);
        using var cts = new CancellationTokenSource();

        var run = eng.RunAsync(
            new EffectParams(EffectType.Flash, Rgb.White,
                FlashInterval: TimeSpan.FromMilliseconds(20), Continuous: false),
            cts.Token);

        bool reached = await WaitForColorAsync(transport, (0, 0, 0));
        cts.Cancel();
        await run;

        Assert.True(reached);
    }

    [Fact]
    public async Task 連続エフェクトはキャンセルまで動き_キャンセルで例外なく終了する()
    {
        var eng = NewEngine(out var transport);
        using var cts = new CancellationTokenSource();

        var run = eng.RunAsync(
            new EffectParams(EffectType.Flash, Rgb.White,
                FlashInterval: TimeSpan.FromMilliseconds(20), Continuous: true),
            cts.Token);

        await Task.Delay(120);
        Assert.False(run.IsCompleted, "連続エフェクトはキャンセルされるまで継続する");

        cts.Cancel();
        await run; // 例外は伝播しない（内部で OCE を握る）

        Assert.True(transport.Entries.Count > 0, "点滅フレームが送出されている");
    }

    [Fact]
    public async Task 連続FadeInは補間フレームを繰り返し送出する()
    {
        var eng = NewEngine(out var transport);
        using var cts = new CancellationTokenSource();

        var run = eng.RunAsync(
            new EffectParams(EffectType.FadeIn, Rgb.Blue,
                CycleDuration: TimeSpan.FromMilliseconds(60), FadeSteps: 10, Continuous: true),
            cts.Token);

        await Task.Delay(200);
        cts.Cancel();
        await run;

        // Black(0,0,0) と Blue(0,0,255) 双方のフレームが現れる＝黒→青の補間が回っている。
        var colors = transport.Entries.Where(e => e.Packet[0] == 0xA2)
            .Select(e => (e.Packet[2], e.Packet[3], e.Packet[4])).ToList();
        Assert.Contains(((byte)0, (byte)0, (byte)0), colors);
        Assert.Contains(((byte)0, (byte)0, (byte)255), colors);
    }
}
