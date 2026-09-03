using SynchrolightAPI.Domain;

namespace SynchrolightAPI.Core.Tests;

/// <summary>RGB値オブジェクト(Rgb)の単体テスト。純ロジック（実機不要）。</summary>
public class RgbTests
{
    [Fact]
    public void 定義済み色定数()
    {
        Assert.Equal(new Rgb(0xFF, 0x00, 0x00), Rgb.Red);
        Assert.Equal(new Rgb(0x00, 0xFF, 0x00), Rgb.Green);
        Assert.Equal(new Rgb(0x00, 0x00, 0xFF), Rgb.Blue);
        Assert.Equal(new Rgb(0xFF, 0xFF, 0xFF), Rgb.White);
        Assert.Equal(new Rgb(0x00, 0x00, 0x00), Rgb.Black);
    }

    [Fact]
    public void ToTuple_RGBの順で返す()
    {
        var (r, g, b) = new Rgb(1, 2, 3).ToTuple();
        Assert.Equal(1, r);
        Assert.Equal(2, g);
        Assert.Equal(3, b);
    }

    [Fact]
    public void 値の等価性()
    {
        Assert.Equal(new Rgb(10, 20, 30), new Rgb(10, 20, 30));
        Assert.NotEqual(new Rgb(10, 20, 30), new Rgb(10, 20, 31));
    }
}
