using SynchrolightAPI.Protocol;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// 送信コマンド・プロトコル(LightProtocol)の単体テスト。
/// 対象仕様: 「送信コマンド・プロトコル一覧」全般（全端末一斉制御 field=0x00 含む）。
/// チェックサム・フレーム配置・バイト順・引数検証を検証する（純ロジック＝実機不要）。
/// </summary>
public class LightProtocolTests
{
    // 32バイトフレームのチェックサム（先頭31バイト合計の下位8bit）を再計算して末尾と一致することを確認する共通ヘルパ。
    private static void AssertValidChecksum32(byte[] frame)
    {
        Assert.Equal(32, frame.Length);
        int sum = 0;
        for (int i = 0; i < 31; i++) sum += frame[i];
        Assert.Equal((byte)(sum & 0xFF), frame[31]);
    }

    // ───────────────────────── チェックサム ─────────────────────────

    [Fact]
    public void CalcChecksum4_下位8bitを返す()
    {
        // 0xFA + 0x01 + 0x02 = 0xFD
        Assert.Equal((byte)0xFD, LightProtocol.CalcChecksum4(0xFA, 0x01, 0x02));
    }

    [Fact]
    public void CalcChecksum4_オーバーフローは下位8bitで折り返す()
    {
        // 0xFF*3 = 765 = 0x2FD → 下位8bit = 0xFD
        Assert.Equal((byte)0xFD, LightProtocol.CalcChecksum4(0xFF, 0xFF, 0xFF));
    }

    [Fact]
    public void CalcChecksum32_先頭31バイト合計の下位8bit()
    {
        var f = new byte[32];
        f[0] = 0xA2; f[1] = 0x01; f[2] = 0xFF; // 合計 = 0x1A2 → 0xA2
        Assert.Equal((byte)0xA2, LightProtocol.CalcChecksum32(f));
    }

    [Fact]
    public void CalcChecksum32_nullは例外()
        => Assert.Throws<ArgumentNullException>(() => LightProtocol.CalcChecksum32(null!));

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void CalcChecksum32_32バイト以外は例外(int len)
        => Assert.Throws<ArgumentException>(() => LightProtocol.CalcChecksum32(new byte[len]));

    // ───────────────────────── 送信機設定 FA / FB ─────────────────────────

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void BuildTxSetChannel_正常範囲(byte ch)
    {
        var f = LightProtocol.BuildTxSetChannel(ch);
        Assert.Equal(4, f.Length);
        Assert.Equal(0xFA, f[0]);
        Assert.Equal(0x01, f[1]);
        Assert.Equal(ch, f[2]);
        Assert.Equal((byte)((0xFA + 0x01 + ch) & 0xFF), f[3]);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)5)]
    public void BuildTxSetChannel_範囲外は例外(byte ch)
        => Assert.Throws<ArgumentOutOfRangeException>(() => LightProtocol.BuildTxSetChannel(ch));

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void BuildTxSetPower_正常範囲(byte pwr)
    {
        var f = LightProtocol.BuildTxSetPower(pwr);
        Assert.Equal(4, f.Length);
        Assert.Equal(0xFB, f[0]);
        Assert.Equal(0x01, f[1]);
        Assert.Equal(pwr, f[2]);
        Assert.Equal((byte)((0xFB + 0x01 + pwr) & 0xFF), f[3]);
    }

    [Fact]
    public void BuildTxSetPower_範囲外は例外()
        => Assert.Throws<ArgumentOutOfRangeException>(() => LightProtocol.BuildTxSetPower(4));

    // ───────────────────────── A2 全体一括色 ─────────────────────────

    [Theory]
    [InlineData((byte)0x00)] // ID未書き込み端末（全端末一斉／夏ID無し版）
    [InlineData((byte)0x01)] // ID書き込み済み端末
    public void BuildA2_GlobalColor_ヘッダとfieldとRGB(byte field)
    {
        var f = LightProtocol.BuildA2_GlobalColor(field, 0x12, 0x34, 0x56);
        Assert.Equal(0xA2, f[0]);
        Assert.Equal(field, f[1]);
        Assert.Equal(0x12, f[2]);
        Assert.Equal(0x34, f[3]);
        Assert.Equal(0x56, f[4]);
        AssertValidChecksum32(f);
    }

    // ───────────────────────── A1 シーケンス再生（フレーム番号 little-endian）─────────────────────────

    [Fact]
    public void BuildA1_PlaySequence_フレーム番号はリトルエンディアン()
    {
        var f = LightProtocol.BuildA1_PlaySequence(0x12345678u);
        Assert.Equal(0xA1, f[0]);
        Assert.Equal(0x01, f[1]);
        Assert.Equal(0x78, f[2]);
        Assert.Equal(0x56, f[3]);
        Assert.Equal(0x34, f[4]);
        Assert.Equal(0x12, f[5]);
        AssertValidChecksum32(f);
    }

    // ───────────────────────── A6 / AD 受信端チャンネル設定（2026-07-24 訂正仕様：場次=0x00）─────────────────────────
    // 回帰ガード：07-16 CH全滅の原因だった旧パラメータ（場次≠0x00・開始行0x0000・長さ0xFF）に戻っていないこと。

    [Theory]
    [InlineData((byte)2, (byte)0xA7)]
    [InlineData((byte)3, (byte)0xA8)]
    [InlineData((byte)4, (byte)0xA9)]
    public void BuildA6_SetRxChannel_固定フレームとチェックサム(byte ch, byte expectedCs)
    {
        var f = LightProtocol.BuildA6_SetRxChannel(ch);
        Assert.Equal(0xA6, f[0]);
        Assert.Equal(0x00, f[1]); // 場次（0xFF→0x00 訂正済み。回帰ガード）
        Assert.Equal(0xFF, f[2]); // 開始行 Hi
        Assert.Equal(0xFF, f[3]); // 開始行 Lo
        Assert.Equal(0x01, f[4]); // 長さ
        Assert.Equal(ch, f[5]);   // ch
        Assert.Equal(expectedCs, f[31]);
        AssertValidChecksum32(f);
    }

    [Theory]
    [InlineData((byte)2, (byte)0xAE)]
    [InlineData((byte)3, (byte)0xAF)]
    [InlineData((byte)4, (byte)0xB0)]
    public void BuildAD_SetRxChannel_固定フレームとチェックサム(byte ch, byte expectedCs)
    {
        var f = LightProtocol.BuildAD_SetRxChannel(ch);
        Assert.Equal(0xAD, f[0]);
        Assert.Equal(0x00, f[1]); // 場次（0xFF→0x00 訂正済み。回帰ガード）
        Assert.Equal(0xFF, f[2]);
        Assert.Equal(0xFF, f[3]);
        Assert.Equal(0x01, f[4]);
        Assert.Equal(ch, f[5]);
        Assert.Equal(expectedCs, f[31]);
        AssertValidChecksum32(f);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)5)]
    public void BuildA6_AD_範囲外chは例外(byte ch)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LightProtocol.BuildA6_SetRxChannel(ch));
        Assert.Throws<ArgumentOutOfRangeException>(() => LightProtocol.BuildAD_SetRxChannel(ch));
    }

    // ───────────────────────── A3 行 / A4 列（単色・複色）─────────────────────────

    [Fact]
    public void BuildA3_Rows_単色_開始位置はビッグエンディアン()
    {
        var f = LightProtocol.BuildA3_Rows(0x00, 0x0102, 5, 0x10, 0x20, 0x30);
        Assert.Equal(0xA3, f[0]);
        Assert.Equal(0x00, f[1]);
        Assert.Equal(0x01, f[2]); // startPos Hi
        Assert.Equal(0x02, f[3]); // startPos Lo
        Assert.Equal(5, f[4]);
        Assert.Equal(0x10, f[5]);
        Assert.Equal(0x20, f[6]);
        Assert.Equal(0x30, f[7]);
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildA3_Rows_複色_色配列を格納()
    {
        var colors = new (byte, byte, byte)[] { (1, 2, 3), (4, 5, 6) };
        var f = LightProtocol.BuildA3_Rows(0x00, 0x0000, 2, colors);
        Assert.Equal(0xA3, f[0]);
        Assert.Equal(2, f[4]);
        Assert.Equal(1, f[5]); Assert.Equal(2, f[6]); Assert.Equal(3, f[7]);
        Assert.Equal(4, f[8]); Assert.Equal(5, f[9]); Assert.Equal(6, f[10]);
        AssertValidChecksum32(f);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)9)]
    public void BuildA3_Rows_複色_len範囲外は例外(byte len)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => LightProtocol.BuildA3_Rows(0x00, 0, len, new (byte, byte, byte)[len == 0 ? 1 : len]));

    [Fact]
    public void BuildA3_Rows_複色_色数不一致は例外()
        => Assert.Throws<ArgumentException>(
            () => LightProtocol.BuildA3_Rows(0x00, 0, 2, new (byte, byte, byte)[] { (1, 1, 1) }));

    [Fact]
    public void BuildA4_Cols_単色()
    {
        var f = LightProtocol.BuildA4_Cols(0x00, 0x00FF, 3, 0xAA, 0xBB, 0xCC);
        Assert.Equal(0xA4, f[0]);
        Assert.Equal(0x00, f[2]);
        Assert.Equal(0xFF, f[3]);
        Assert.Equal(3, f[4]);
        Assert.Equal(0xAA, f[5]); Assert.Equal(0xBB, f[6]); Assert.Equal(0xCC, f[7]);
        AssertValidChecksum32(f);
    }

    // ───────────────────────── A0 ポイント制御 ─────────────────────────

    [Fact]
    public void BuildA0_Points_行列開始とRGB()
    {
        var colors = new (byte, byte, byte)[] { (9, 8, 7) };
        var f = LightProtocol.BuildA0_Points(0x00, 0x0201, 0x0403, 1, colors);
        Assert.Equal(0xA0, f[0]);
        Assert.Equal(0x02, f[2]); Assert.Equal(0x01, f[3]); // row big-endian
        Assert.Equal(0x04, f[4]); Assert.Equal(0x03, f[5]); // col big-endian
        Assert.Equal(1, f[6]);
        Assert.Equal(9, f[7]); Assert.Equal(8, f[8]); Assert.Equal(7, f[9]);
        AssertValidChecksum32(f);
    }

    // ───────────────────────── A8 左右エリア / AA 複数行同色 ─────────────────────────

    [Fact]
    public void BuildA8_MultiColsSameColor()
    {
        var f = LightProtocol.BuildA8_MultiColsSameColor(0x00, 0x0010, 0x0020, 1, 2, 3);
        Assert.Equal(0xA8, f[0]);
        Assert.Equal(0x00, f[2]); Assert.Equal(0x10, f[3]);
        Assert.Equal(0x00, f[4]); Assert.Equal(0x20, f[5]);
        Assert.Equal(1, f[6]); Assert.Equal(2, f[7]); Assert.Equal(3, f[8]);
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildAA_MultiRowsSameColor()
    {
        var f = LightProtocol.BuildAA_MultiRowsSameColor(0x00, 0x0011, 0x0022, 4, 5, 6);
        Assert.Equal(0xAA, f[0]);
        Assert.Equal(0x00, f[2]); Assert.Equal(0x11, f[3]);
        Assert.Equal(0x00, f[4]); Assert.Equal(0x22, f[5]);
        Assert.Equal(4, f[6]); Assert.Equal(5, f[7]); Assert.Equal(6, f[8]);
        AssertValidChecksum32(f);
    }

    // ───────────────────────── AC / AE ブロック制御 ─────────────────────────

    [Fact]
    public void BuildAC_BlockColor()
    {
        var f = LightProtocol.BuildAC_BlockColor(0x02, 0x05, 0x11, 0x22, 0x33);
        Assert.Equal(0xAC, f[0]);
        Assert.Equal(0x02, f[1]); Assert.Equal(0x05, f[2]);
        Assert.Equal(0x11, f[3]); Assert.Equal(0x22, f[4]); Assert.Equal(0x33, f[5]);
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildAE_BlockColorSector()
    {
        var f = LightProtocol.BuildAE_BlockColorSector(0x02, 0x05, 0x11, 0x22, 0x33);
        Assert.Equal(0xAE, f[0]);
        AssertValidChecksum32(f);
    }

    // ───────────────────────── A9 ファイル書き込み / A7 データ ─────────────────────────

    [Fact]
    public void BuildA9_FileWriteStart_データ長はビッグエンディアン()
    {
        var f = LightProtocol.BuildA9_FileWriteStart(0x1234);
        Assert.Equal(0xA9, f[0]);
        Assert.Equal(0x01, f[1]);
        Assert.Equal(0x00, f[2]);
        Assert.Equal(0x01, f[3]);
        Assert.Equal(0x12, f[4]); // len Hi
        Assert.Equal(0x34, f[5]); // len Lo
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildA7_FileWriteData_フレーム番号はリトルエンディアン()
    {
        var colors = new (byte, byte, byte)[] { (1, 2, 3), (4, 5, 6) };
        var f = LightProtocol.BuildA7_FileWriteData(0x0102, 0x0304, 2, 0xAABBCCDDu, colors);
        Assert.Equal(0xA7, f[0]);
        Assert.Equal(0xFF, f[1]); // 場次
        Assert.Equal(0x01, f[2]); Assert.Equal(0x02, f[3]); // row big-endian
        Assert.Equal(0x03, f[4]); Assert.Equal(0x04, f[5]); // col big-endian
        Assert.Equal(2, f[6]);
        Assert.Equal(0xDD, f[7]); Assert.Equal(0xCC, f[8]); Assert.Equal(0xBB, f[9]); Assert.Equal(0xAA, f[10]); // frameNo little-endian
        Assert.Equal(1, f[11]); Assert.Equal(2, f[12]); Assert.Equal(3, f[13]);
        Assert.Equal(4, f[14]); Assert.Equal(5, f[15]); Assert.Equal(6, f[16]);
        AssertValidChecksum32(f);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)7)]
    public void BuildA7_FileWriteData_len範囲外は例外(byte len)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => LightProtocol.BuildA7_FileWriteData(0, 0, len, 0, new (byte, byte, byte)[len == 0 ? 1 : len]));

    // ───────────────────────── A9 レインボー（V4.5: 3.15-3.22）─────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void BuildA9_SetRainbowColors_色数2から7は正常(int n)
    {
        var colors = new (byte, byte, byte)[n];
        for (int i = 0; i < n; i++) colors[i] = ((byte)i, (byte)i, (byte)i);
        var f = LightProtocol.BuildA9_SetRainbowColors(colors);
        Assert.Equal(0xA9, f[0]);
        Assert.Equal(0x02, f[1]);
        Assert.Equal((byte)n, f[2]);
        AssertValidChecksum32(f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void BuildA9_SetRainbowColors_色数範囲外は例外(int n)
        => Assert.Throws<ArgumentException>(
            () => LightProtocol.BuildA9_SetRainbowColors(new (byte, byte, byte)[n]));

    [Fact]
    public void BuildA9_RainbowSolid_常時点灯()
    {
        var f = LightProtocol.BuildA9_RainbowSolid(0x03);
        Assert.Equal(0xA9, f[0]); Assert.Equal(0x03, f[1]); Assert.Equal(0x00, f[2]); Assert.Equal(0x03, f[3]);
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildA9_RainbowBlink_周期はビッグエンディアン()
    {
        var f = LightProtocol.BuildA9_RainbowBlink(0x02, 0x03E8, 7); // 1000ms
        Assert.Equal(0xA9, f[0]); Assert.Equal(0x03, f[1]); Assert.Equal(0x01, f[2]);
        Assert.Equal(0x02, f[3]);
        Assert.Equal(0x03, f[4]); Assert.Equal(0xE8, f[5]); // period big-endian
        Assert.Equal(7, f[6]);
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildA9_RainbowFadeInOut_FI_FO時間()
    {
        var f = LightProtocol.BuildA9_RainbowFadeInOut(0x01, 0x0100, 0x0200);
        Assert.Equal(0xA9, f[0]); Assert.Equal(0x03, f[1]); Assert.Equal(0x02, f[2]); Assert.Equal(0x01, f[3]);
        Assert.Equal(0x01, f[4]); Assert.Equal(0x00, f[5]); // FI big-endian
        Assert.Equal(0x02, f[6]); Assert.Equal(0x00, f[7]); // FO big-endian
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildA9_RainbowFadeIn_時間()
    {
        var f = LightProtocol.BuildA9_RainbowFadeIn(0x01, 0x0400);
        Assert.Equal(0x03, f[1]); Assert.Equal(0x03, f[2]);
        Assert.Equal(0x04, f[4]); Assert.Equal(0x00, f[5]);
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildA9_RainbowFadeOut_時間()
    {
        var f = LightProtocol.BuildA9_RainbowFadeOut(0x01, 0x0400);
        Assert.Equal(0x03, f[1]); Assert.Equal(0x04, f[2]);
        Assert.Equal(0x04, f[4]); Assert.Equal(0x00, f[5]);
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildA9_RainbowRandom_7色ランダム点滅()
    {
        var f = LightProtocol.BuildA9_RainbowRandom(0x02);
        Assert.Equal(0x03, f[1]); Assert.Equal(0x05, f[2]); Assert.Equal(0x02, f[3]);
        AssertValidChecksum32(f);
    }

    [Fact]
    public void BuildA9_RainbowPause_一時停止()
    {
        var f = LightProtocol.BuildA9_RainbowPause();
        Assert.Equal(0xA9, f[0]); Assert.Equal(0x04, f[1]);
        AssertValidChecksum32(f);
    }

    // ───────────────────────── ToHex ─────────────────────────

    [Fact]
    public void ToHex_大文字2桁スペース区切り()
        => Assert.Equal("A2 01 FF 00", LightProtocol.ToHex(new byte[] { 0xA2, 0x01, 0xFF, 0x00 }));
}
