using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SynchrolightAPI.Core.Tests.TestSupport;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Models;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// シーケンス再生エンジン(SequencePlayer)の挙動テスト。
/// 実 EffectEngine/EffectScheduler + RecordingTransport（実機不要）で、
/// 時刻順ソート・IsPlaying/CurrentStepIndex のライフサイクル・キャンセル後の状態クリアを検証する。
/// 実タイミング精度（何 ms で発火したか）は実機/統合確認項目。
/// </summary>
public class SequencePlayerTests
{
    private static SequencePlayer NewPlayer(out RecordingTransport transport)
    {
        transport = new RecordingTransport();
        var scheduler = new EffectScheduler(transport, new InterpolationService(), NullLogger<EffectScheduler>.Instance);
        var engine = new EffectEngine(scheduler, NullLogger<EffectEngine>.Instance);
        return new SequencePlayer(transport, engine, scheduler, NullLogger<SequencePlayer>.Instance);
    }

    private static SequenceStep Color(int timeMs, byte r, byte g, byte b) => new()
    {
        TimeOffsetMs = timeMs,
        CommandType = SequenceCommandType.Color,
        R = r,
        G = g,
        B = b,
    };

    [Fact]
    public async Task 空シーケンスは即終了しIsPlayingは偽のまま()
    {
        var player = NewPlayer(out _);
        var sw = Stopwatch.StartNew();
        await player.PlayAsync(new Sequence { Name = "empty", Steps = new() }, CancellationToken.None);
        sw.Stop();

        Assert.False(player.IsPlaying);
        Assert.True(sw.ElapsedMilliseconds < 1000, "空シーケンスは待機せず即戻る");
    }

    [Fact]
    public async Task ステップは時刻順に実行される_登録順が乱れていてもTimeOffset昇順()
    {
        var player = NewPlayer(out var transport);
        using var cts = new CancellationTokenSource();

        // 登録順は乱れている（200→0→100）が、実行は 0(緑)→100(赤)→200(青) の順であるべき。
        var seq = new Sequence
        {
            Name = "order",
            Steps = new()
            {
                Color(200, 0, 0, 255),  // 青
                Color(0,   0, 255, 0),  // 緑
                Color(100, 255, 0, 0),  // 赤
            }
        };

        var run = player.PlayAsync(seq, cts.Token);
        await Task.Delay(400); // 全ステップ発火済み

        Assert.Equal(3, player.TotalStepCount);
        Assert.True(player.IsPlaying);

        // 送出色の初出順を取り出す
        var order = new List<(byte, byte, byte)>();
        foreach (var e in transport.Entries.Where(x => x.Packet[0] == 0xA2))
        {
            var c = (e.Packet[2], e.Packet[3], e.Packet[4]);
            if (order.Count == 0 || order[^1] != c) { if (!order.Contains(c)) order.Add(c); }
        }
        int gi = order.IndexOf((0, 255, 0)); // 緑
        int ri = order.IndexOf((255, 0, 0)); // 赤
        int bi = order.IndexOf((0, 0, 255)); // 青
        Assert.True(gi >= 0 && ri >= 0 && bi >= 0, "3色すべて送出される");
        Assert.True(gi < ri && ri < bi, $"時刻順 緑→赤→青 で送出されること (g={gi},r={ri},b={bi})");

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task キャンセルで再生状態がクリアされる()
    {
        var player = NewPlayer(out _);
        using var cts = new CancellationTokenSource();

        var seq = new Sequence { Name = "cancel", Steps = new() { Color(0, 255, 255, 255), Color(5000, 0, 0, 0) } };
        var run = player.PlayAsync(seq, cts.Token);
        await Task.Delay(80);
        Assert.True(player.IsPlaying);

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.False(player.IsPlaying);
        Assert.Equal(-1, player.CurrentStepIndex);
        Assert.Equal(0, player.TotalStepCount);
    }

    [Fact]
    public void FlushAndStop_例外なくスケジューラをAbortする()
    {
        var player = NewPlayer(out var transport);
        player.FlushAndStop();
        Assert.True(transport.FlushCount >= 1);
    }

    // ── BUG-20260903-02: Flash→Rainbow で前色が一瞬残る（再発）＝黒ラッチを設定待ちの間 保持送信する ──

    private static bool IsBlackA2(RecordingTransport.Entry e) =>
        e.Packet.Length >= 5 && e.Packet[0] == 0xA2 &&
        e.Packet[2] == 0 && e.Packet[3] == 0 && e.Packet[4] == 0;

    private static bool IsModeFrame(RecordingTransport.Entry e) =>
        e.Packet.Length >= 2 && e.Packet[0] == 0xA9 && e.Packet[1] == 0x03;

    private static bool IsPalette(RecordingTransport.Entry e) =>
        e.Packet.Length >= 2 && e.Packet[0] == 0xA9 && e.Packet[1] == 0x02;

    [Fact]
    public async Task Rainbow開始時は設定待ちの間ずっと黒を保持送信する_BUG20260903_02()
    {
        var player = NewPlayer(out var transport);
        using var cts = new CancellationTokenSource();

        var seq = new Sequence
        {
            Name = "rainbow",
            Steps = new()
            {
                new SequenceStep
                {
                    TimeOffsetMs = 0,
                    CommandType = SequenceCommandType.Rainbow,
                    RainbowMode = 2,               // FI/FO
                    RainbowFadeInMs = 1000,
                    RainbowFadeOutMs = 1000,
                    RainbowCycleDurationMs = 3000,
                    Field = 0x01,
                },
            }
        };

        var run = player.PlayAsync(seq, cts.Token);
        await Task.Delay(150);   // 黒保持ループ(~50ms)＋先頭フレームを確実に通過
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { } catch { }

        var entries = transport.Entries.ToList();

        // 黒A2が複数回・高優先で送られる（1発＋設定待ちの保持ループ）＝RF欠落補償/割り込み塗り潰し。
        int blackCount = entries.Count(e => IsBlackA2(e) && e.Options.HighPriority);
        Assert.True(blackCount >= 3, $"黒ラッチは設定待ちの間 保持送信され複数回になる: {blackCount}");

        // 先頭フレーム(A9 03)より前に黒が来ている（前色を消してからレインボー開始）。
        int firstBlack = entries.FindIndex(IsBlackA2);
        int firstFrame = entries.FindIndex(IsModeFrame);
        Assert.True(firstBlack >= 0, "黒ラッチが送出される");
        Assert.True(firstFrame < 0 || firstBlack < firstFrame, "黒ラッチは先頭フレームより前");
    }

    [Fact]
    public async Task Rainbow開始時は前ステップが別場次なら両場次に黒を送る_BUG20260903_02()
    {
        var player = NewPlayer(out var transport);
        using var cts = new CancellationTokenSource();

        var seq = new Sequence
        {
            Name = "field-mismatch",
            Steps = new()
            {
                // 前ステップ(別場次 0x02)で点灯 → その後 Rainbow(場次 0x01)。
                new SequenceStep { TimeOffsetMs = 0, CommandType = SequenceCommandType.Color, R = 255, G = 0, B = 0, Field = 0x02 },
                new SequenceStep
                {
                    TimeOffsetMs = 40,
                    CommandType = SequenceCommandType.Rainbow,
                    RainbowMode = 2, RainbowFadeInMs = 1000, RainbowFadeOutMs = 1000, RainbowCycleDurationMs = 3000,
                    Field = 0x01,
                },
            }
        };

        var run = player.PlayAsync(seq, cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { } catch { }

        var entries = transport.Entries.ToList();
        bool IsBlackA2OnField(RecordingTransport.Entry e, byte field) =>
            e.Packet.Length >= 5 && e.Packet[0] == 0xA2 && e.Packet[1] == field &&
            e.Packet[2] == 0 && e.Packet[3] == 0 && e.Packet[4] == 0;

        Assert.Contains(entries, e => IsBlackA2OnField(e, 0x01));  // Rainbow ステップ場次
        Assert.Contains(entries, e => IsBlackA2OnField(e, 0x02));  // 前ステップ(別場次)にも黒
    }

    [Fact]
    public async Task Rainbow開始時は黒をパレット送出より前に確定する_BUG20260903_02()
    {
        // v2.8.0でも再現→送出順序を「黒保持(パレット前) → パレット → 短い保持 → 先頭フレーム」に見直した。
        // 端末が A9 02 受信後に A2 ダイレクト色を無視しても暗転が保たれるよう、黒はパレットより前に確定する。
        var player = NewPlayer(out var transport);
        using var cts = new CancellationTokenSource();

        var seq = new Sequence
        {
            Name = "black-before-palette",
            Steps = new()
            {
                new SequenceStep
                {
                    TimeOffsetMs = 0,
                    CommandType = SequenceCommandType.Rainbow,
                    RainbowMode = 2, RainbowFadeInMs = 1000, RainbowFadeOutMs = 1000, RainbowCycleDurationMs = 3000,
                    Field = 0x01,
                },
            }
        };

        var run = player.PlayAsync(seq, cts.Token);
        await Task.Delay(150);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { } catch { }

        var entries = transport.Entries.ToList();

        int firstBlack = entries.FindIndex(IsBlackA2);
        int firstPalette = entries.FindIndex(IsPalette);
        int firstFrame = entries.FindIndex(IsModeFrame);

        Assert.True(firstBlack >= 0, "黒ラッチが送出される");
        Assert.True(firstPalette >= 0, "パレット(A9 02)が送出される");
        // 黒はパレット(A9 02)より前に確定する（A9 02 後に A2 が無効でも暗転を維持できるように）。
        Assert.True(firstBlack < firstPalette, $"黒はパレット(A9 02)より前に送る: black={firstBlack} palette={firstPalette}");
        // パレット→先頭フレームの順は維持（パレット未設定で先頭フレームを描かない）。
        Assert.True(firstFrame < 0 || firstPalette < firstFrame, "パレットは先頭フレームより前");
    }
}
