namespace SynchrolightAPI.Domain;

/// <summary>
/// ライト制御対象を表す discriminated union
/// </summary>
public abstract record Target
{
    private Target() { }

    /// <summary>全体一括 (A2)</summary>
    public sealed record All : Target;

    /// <summary>フィールドゾーン定義・水平操作 (A3): field, 開始行, 行数</summary>
    public sealed record Rows(byte Field, ushort StartRow, byte Len) : Target;

    /// <summary>列データ・垂直操作 (A4): field, 開始列, 列数</summary>
    public sealed record Cols(byte Field, ushort StartCol, byte Len) : Target;

    /// <summary>ポイント制御 (A0): field, 開始行, 開始列, 個数</summary>
    public sealed record Points(byte Field, ushort StartRow, ushort StartCol, byte Len) : Target;

    /// <summary>左右エリア色制御 (A8): field, 開始列, 列数</summary>
    public sealed record MultiCols(byte Field, ushort StartCol, ushort ColLen) : Target;

    /// <summary>複数行同色制御 (AA): field, 開始行, 行数</summary>
    public sealed record MultiRows(byte Field, ushort StartRow, ushort RowLen) : Target;

    /// <summary>ブロック制御・セクター無効 (AC): プログラム番号, ブロック番号</summary>
    public sealed record Block(byte ProgNo, byte BlockNo) : Target;

    /// <summary>ブロック制御・セクター有効 (AE): プログラム番号, ブロック番号</summary>
    public sealed record BlockSector(byte ProgNo, byte BlockNo) : Target;
}
