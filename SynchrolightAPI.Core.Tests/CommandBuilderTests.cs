using SynchrolightAPI.Domain;
using SynchrolightAPI.Protocol;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// コマンドビルダ(CommandBuilder)の単体テスト。
/// 対象仕様: 制御対象(Target)種別に応じた適切なプロトコルコマンド選択。純ロジック（実機不要）。
/// </summary>
public class CommandBuilderTests
{
    private readonly CommandBuilder _builder = new();

    [Fact]
    public void All_field0はA2ブロードキャスト()
    {
        byte[] f = _builder.BuildSetColor(new Target.All(0x00), Rgb.Red);
        Assert.Equal(0xA2, f[0]);
        Assert.Equal(0x00, f[1]);   // field=0x00（全端末一斉／ID未書込）
        Assert.Equal(0xFF, f[2]);   // R
    }

    [Fact]
    public void All_field1はID書込済()
    {
        byte[] f = _builder.BuildSetColor(new Target.All(0x01), Rgb.Green);
        Assert.Equal(0xA2, f[0]);
        Assert.Equal(0x01, f[1]);
        Assert.Equal(0xFF, f[3]);   // G
    }

    [Fact]
    public void Rows_はA3()
    {
        byte[] f = _builder.BuildSetColor(new Target.Rows(0x00, 0x0102, 5), Rgb.Blue);
        Assert.Equal(0xA3, f[0]);
        Assert.Equal(0x01, f[2]); Assert.Equal(0x02, f[3]);
        Assert.Equal(5, f[4]);
    }

    [Fact]
    public void Cols_はA4()
    {
        byte[] f = _builder.BuildSetColor(new Target.Cols(0x00, 0x0000, 3), Rgb.Red);
        Assert.Equal(0xA4, f[0]);
    }

    [Fact]
    public void Points_はA0()
    {
        byte[] f = _builder.BuildSetColor(new Target.Points(0x00, 0, 0, 1), Rgb.Red);
        Assert.Equal(0xA0, f[0]);
    }

    [Fact]
    public void MultiCols_はA8()
    {
        byte[] f = _builder.BuildSetColor(new Target.MultiCols(0x00, 0, 1), Rgb.Red);
        Assert.Equal(0xA8, f[0]);
    }

    [Fact]
    public void MultiRows_はAA()
    {
        byte[] f = _builder.BuildSetColor(new Target.MultiRows(0x00, 0, 1), Rgb.Red);
        Assert.Equal(0xAA, f[0]);
    }

    [Fact]
    public void Block_はAC()
    {
        byte[] f = _builder.BuildSetColor(new Target.Block(0x02, 0x05), Rgb.Red);
        Assert.Equal(0xAC, f[0]);
        Assert.Equal(0x02, f[1]); Assert.Equal(0x05, f[2]);
    }

    [Fact]
    public void BlockSector_はAE()
    {
        byte[] f = _builder.BuildSetColor(new Target.BlockSector(0x02, 0x05), Rgb.Red);
        Assert.Equal(0xAE, f[0]);
    }

    [Fact]
    public void 送信機ch_pwrはLightProtocolへ委譲()
    {
        Assert.Equal(0xFA, _builder.BuildTxSetChannel(2)[0]);
        Assert.Equal(0xFB, _builder.BuildTxSetPower(1)[0]);
    }

    // ───────────────────────── Packet32 ─────────────────────────

    [Fact]
    public void Packet32_32バイト以外は例外()
        => Assert.Throws<ArgumentException>(() => new Packet32(new byte[31]));

    [Fact]
    public void Packet32_暗黙変換が往復する()
    {
        var data = new byte[32];
        data[0] = 0xA2;
        Packet32 p = data;              // byte[] -> Packet32
        byte[] back = p;               // Packet32 -> byte[]
        Assert.Equal(0xA2, back[0]);
    }
}
