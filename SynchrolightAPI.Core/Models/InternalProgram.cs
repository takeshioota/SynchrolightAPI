namespace SynchrolightAPI.Models;

/// <summary>
/// SNO端末の内蔵プログラム定義。
/// Bluetooth経由で事前に書き込まれたアニメーションプログラムに対応する。
/// A1コマンドのフレーム番号で呼び出す。
/// </summary>
public record InternalProgramDefinition(uint FrameNo, string Name, string Description);

/// <summary>
/// 内蔵プログラム定義テーブル。
/// フレーム番号は実機でBluetooth書き込み後に確定するため、
/// 暫定値を定義し、実機確認後に更新する。
/// </summary>
public static class InternalProgramTable
{
    /// <summary>既知の内蔵プログラム一覧</summary>
    public static IReadOnlyList<InternalProgramDefinition> Programs { get; } =
    [
        new(0, "Rainbow",  "虹色循環（7色ローテーション、各端末がランダムオフセットで再生）"),
        new(1, "満点星",    "ランダム星点滅"),
        new(2, "プログラム2", "未定義 — 実機確認後に更新"),
        new(3, "プログラム3", "未定義 — 実機確認後に更新"),
        new(4, "プログラム4", "未定義 — 実機確認後に更新"),
        new(5, "プログラム5", "未定義 — 実機確認後に更新"),
    ];

    /// <summary>フレーム番号からプログラム定義を検索</summary>
    public static InternalProgramDefinition? FindByFrameNo(uint frameNo)
        => Programs.FirstOrDefault(p => p.FrameNo == frameNo);

    /// <summary>名前からプログラム定義を検索</summary>
    public static InternalProgramDefinition? FindByName(string name)
        => Programs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
