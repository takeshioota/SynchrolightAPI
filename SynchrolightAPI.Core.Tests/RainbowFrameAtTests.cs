using SynchrolightAPI.Protocol;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// LightProtocol.RainbowFrameAt（レインボー colorFrameNo の純関数）の単体テスト。
/// 「色フレームは経過時間の純関数であり、送信ループのジッタや負荷で速くならない」という
/// BUG-20260903-01 対策の中核不変条件を回帰から守る（純ロジック＝実機不要）。
/// </summary>
public class RainbowFrameAtTests
{
    [Fact]
    public void 経過0は先頭フレーム0()
    {
        Assert.Equal((byte)0, LightProtocol.RainbowFrameAt(0, 1000, 7));
    }

    [Fact]
    public void 周期ごとに1つ進む()
    {
        // cycle=1000ms, colorCount=7
        Assert.Equal((byte)0, LightProtocol.RainbowFrameAt(0, 1000, 7));
        Assert.Equal((byte)0, LightProtocol.RainbowFrameAt(999, 1000, 7));
        Assert.Equal((byte)1, LightProtocol.RainbowFrameAt(1000, 1000, 7));
        Assert.Equal((byte)1, LightProtocol.RainbowFrameAt(1999, 1000, 7));
        Assert.Equal((byte)2, LightProtocol.RainbowFrameAt(2000, 1000, 7));
    }

    [Fact]
    public void 色数で折り返す()
    {
        // colorCount=2 → 0,1,0,1,...
        Assert.Equal((byte)0, LightProtocol.RainbowFrameAt(0, 500, 2));
        Assert.Equal((byte)1, LightProtocol.RainbowFrameAt(500, 500, 2));
        Assert.Equal((byte)0, LightProtocol.RainbowFrameAt(1000, 500, 2));
        Assert.Equal((byte)1, LightProtocol.RainbowFrameAt(1500, 500, 2));
    }

    [Fact]
    public void 経過時間の純関数_呼び出し方に依存しない()
    {
        // 同じ経過時間なら、途中で何回呼ぼうと常に同じフレーム（＝壁時計固定）。
        // 30〜40秒相当まで進めても「1周期=1増加」を厳密に維持し、加速しないことを確認。
        const int cycle = 1000, colors = 7;
        byte expected = 0;
        for (long t = 0; t <= 60_000; t += 250) // 60秒ぶんを 250ms 刻みで走査
        {
            expected = (byte)(t / cycle % colors);
            Assert.Equal(expected, LightProtocol.RainbowFrameAt(t, cycle, colors));
        }
    }

    [Fact]
    public void フレーム変化回数は経過秒数と周期で決まる_加速しない()
    {
        // cycle=1000ms を 40 秒流すと、フレーム変化はちょうど 40 回（0→1→...）であり、
        // 後半で増える（加速する）ことはない。
        const int cycle = 1000, colors = 7;
        int changes = 0;
        byte last = LightProtocol.RainbowFrameAt(0, cycle, colors);
        for (long t = 1; t <= 40_000; t++)
        {
            byte f = LightProtocol.RainbowFrameAt(t, cycle, colors);
            if (f != last) { changes++; last = f; }
        }
        Assert.Equal(40, changes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void 周期が0以下なら0を返す(int cycle)
    {
        Assert.Equal((byte)0, LightProtocol.RainbowFrameAt(5000, cycle, 7));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void 色数が0以下なら0を返す(int colorCount)
    {
        Assert.Equal((byte)0, LightProtocol.RainbowFrameAt(5000, 1000, colorCount));
    }

    [Fact]
    public void 負の経過時間は0扱い()
    {
        Assert.Equal((byte)0, LightProtocol.RainbowFrameAt(-500, 1000, 7));
    }
}
