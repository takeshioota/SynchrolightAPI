using SynchrolightAPI.Protocol;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// プロトコル生成の耐久・網羅テスト（純ロジック＝実機不要・高速）。
///  ・全フレームビルダを大量に生成してもチェックサム不変条件が崩れないこと
///  ・RainbowFrameAt が超長時間でも壁時計純関数のまま（加速せず・オーバーフローせず）であること
///  ・A9 レインボー色設定が 2〜7 色の全パターンで正しく組まれること
/// を高反復で確認する。
/// </summary>
public class ProtocolEnduranceTests
{
    private static void AssertValidChecksum32(byte[] frame)
    {
        Assert.Equal(32, frame.Length);
        int sum = 0;
        for (int i = 0; i < 31; i++) sum += frame[i];
        Assert.Equal((byte)(sum & 0xFF), frame[31]);
    }

    // ───────────────────────── A2 大量生成 ─────────────────────────

    [Fact]
    public void A2全体一括色_10万通りの入力でチェックサム不変条件が保たれる()
    {
        // 決定的に R/G/B/field を回してチェックサムを検証（RNG 非依存で再現可能）。
        int count = 0;
        for (int i = 0; i < 100_000; i++)
        {
            byte field = (byte)(i & 0x01);
            byte r = (byte)(i & 0xFF);
            byte g = (byte)((i >> 8) & 0xFF);
            byte b = (byte)((i >> 3) & 0xFF);
            AssertValidChecksum32(LightProtocol.BuildA2_GlobalColor(field, r, g, b));
            count++;
        }
        Assert.Equal(100_000, count);
    }

    // ───────────────────────── A9 レインボー 全色数 ─────────────────────────

    [Fact]
    public void A9レインボー色設定_2から7色を反復生成しても正しく組まれる()
    {
        for (int iter = 0; iter < 20_000; iter++)
        {
            int n = 2 + (iter % 6); // 2..7
            var colors = new (byte r, byte g, byte b)[n];
            for (int k = 0; k < n; k++)
                colors[k] = ((byte)(iter + k), (byte)(k * 37), (byte)(iter ^ k));

            var f = LightProtocol.BuildA9_SetRainbowColors(colors);
            Assert.Equal(0xA9, f[0]);
            Assert.Equal(0x02, f[1]);
            Assert.Equal((byte)n, f[2]);
            AssertValidChecksum32(f);
        }
    }

    [Fact]
    public void A9レインボー各モード_反復生成でチェックサム不変()
    {
        for (int i = 0; i < 20_000; i++)
        {
            byte frame = (byte)(i % 7);
            ushort t = (ushort)(256 + (i % 4744)); // 256..4999
            AssertValidChecksum32(LightProtocol.BuildA9_RainbowSolid(frame));
            AssertValidChecksum32(LightProtocol.BuildA9_RainbowBlink(frame, t, (byte)(1 + i % 9)));
            AssertValidChecksum32(LightProtocol.BuildA9_RainbowFadeInOut(frame, t, t));
            AssertValidChecksum32(LightProtocol.BuildA9_RainbowFadeIn(frame, t));
            AssertValidChecksum32(LightProtocol.BuildA9_RainbowFadeOut(frame, t));
            AssertValidChecksum32(LightProtocol.BuildA9_RainbowRandom(frame));
            AssertValidChecksum32(LightProtocol.BuildA9_RainbowPause());
        }
    }

    // ───────────────────────── RainbowFrameAt 超長時間 ─────────────────────────

    [Fact]
    public void RainbowFrameAt_24時間相当を周期刻みで走査しても公式どおり()
    {
        const int cycle = 2000, colors = 7;
        long dayMs = 24L * 3600 * 1000; // 86,400,000ms
        for (long t = 0; t <= dayMs; t += cycle) // 約43,200点
            Assert.Equal((byte)((t / cycle) % colors), LightProtocol.RainbowFrameAt(t, cycle, colors));
    }

    [Fact]
    public void RainbowFrameAt_20万周期を進めても1周期1増加で加速しない()
    {
        const int cycle = 100, colors = 5;
        for (long k = 0; k <= 200_000; k++)
            Assert.Equal((byte)(k % colors), LightProtocol.RainbowFrameAt(k * cycle, cycle, colors));
    }

    [Theory]
    [InlineData(1_000_000_000L)]      // 約11.5日
    [InlineData(1_000_000_000_000L)]  // 約31年
    [InlineData(long.MaxValue / 2)]
    public void RainbowFrameAt_極大の経過時間でもオーバーフローせず公式どおり(long elapsedMs)
    {
        const int cycle = 1234, colors = 7;
        byte expected = (byte)((elapsedMs / cycle) % colors);
        Assert.Equal(expected, LightProtocol.RainbowFrameAt(elapsedMs, cycle, colors));
    }

    // ───────────────────────── チェックサム再計算ラウンドトリップ ─────────────────────────

    [Fact]
    public void CalcChecksum4_全256通りの入力で下位8bit一致()
    {
        for (int a = 0; a < 256; a++)
            for (int b = 0; b < 256; b++)
            {
                byte c = (byte)((a * 7) & 0xFF);
                byte expected = (byte)((a + b + c) & 0xFF);
                Assert.Equal(expected, LightProtocol.CalcChecksum4((byte)a, (byte)b, c));
            }
    }
}
