namespace SynchrolightAPI.Domain;

/// <summary>
/// RGB色の値オブジェクト
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Red   = new(0xFF, 0x00, 0x00);
    public static readonly Rgb Green = new(0x00, 0xFF, 0x00);
    public static readonly Rgb Blue  = new(0x00, 0x00, 0xFF);
    public static readonly Rgb White = new(0xFF, 0xFF, 0xFF);
    public static readonly Rgb Black = new(0x00, 0x00, 0x00);

    /// <summary>RGB値をタプルに変換</summary>
    public (byte r, byte g, byte b) ToTuple() => (R, G, B);
}
