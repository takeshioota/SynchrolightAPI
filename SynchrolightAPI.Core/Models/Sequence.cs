using SynchrolightAPI.Services;

namespace SynchrolightAPI.Models;

/// <summary>
/// シーケンス: 時間順に並んだコマンドステップの集合。
/// PC側で保存し、ワンクリックで再生する。
/// 仕様: 3.4 シーケンス再生
/// </summary>
public class Sequence
{
    /// <summary>シーケンス名（表示用）</summary>
    public string Name { get; set; } = "";

    /// <summary>ステップ一覧（時刻順）</summary>
    public List<SequenceStep> Steps { get; set; } = [];
}

/// <summary>
/// シーケンスステップ: 特定時刻に実行するコマンド
/// </summary>
public class SequenceStep
{
    /// <summary>シーケンス開始からの経過時間（ミリ秒）</summary>
    public int TimeOffsetMs { get; set; }

    /// <summary>コマンド種別</summary>
    public SequenceCommandType CommandType { get; set; }

    /// <summary>色 R</summary>
    public byte R { get; set; }

    /// <summary>色 G</summary>
    public byte G { get; set; }

    /// <summary>色 B</summary>
    public byte B { get; set; }

    /// <summary>フィールド値</summary>
    public byte Field { get; set; } = 0x00;

    /// <summary>再送回数（null=グローバル設定に従う）</summary>
    public int? RetransmitCount { get; set; }

    /// <summary>エフェクト種別（CommandType=Effect時のみ有効）</summary>
    public EffectType? EffectType { get; set; }

    /// <summary>エフェクト周期（ms）</summary>
    public int? EffectCycleDurationMs { get; set; }

    /// <summary>Fade補間ステップ数</summary>
    public int? FadeSteps { get; set; }

    /// <summary>連続再生するか（false=単発実行後に最終色保持）。デフォルト: true</summary>
    public bool Continuous { get; set; } = true;

    /// <summary>内蔵プログラムのフレーム番号（CommandType=InternalProgram時のみ有効）</summary>
    public uint? FrameNo { get; set; }

    // 2026-05-30 追加: スムーズ遷移サポート (A1)
    /// <summary>
    /// 行間遷移時間（ミリ秒）。0 または null = 即時切替、N > 0 = N ミリ秒かけて前ステップの色から滑らかに遷移。
    /// </summary>
    public int? TransitionMs { get; set; }

    // 2026-05-30 追加: 2色カラーサポート (A2)
    /// <summary>2色目 R（CommandType=Color2 時のみ有効）</summary>
    public byte? R2 { get; set; }

    /// <summary>2色目 G（CommandType=Color2 時のみ有効）</summary>
    public byte? G2 { get; set; }

    /// <summary>2色目 B（CommandType=Color2 時のみ有効）</summary>
    public byte? B2 { get; set; }

    /// <summary>BPM（CommandType=Color2 時のみ有効）。周期(ms) = 60000 / BPM</summary>
    public int? Bpm { get; set; }
}

/// <summary>
/// シーケンスで実行するコマンドの種別
/// </summary>
public enum SequenceCommandType
{
    /// <summary>色設定（A2グローバル）</summary>
    Color,
    /// <summary>消灯</summary>
    Off,
    /// <summary>エフェクト開始（EffectType指定）</summary>
    Effect,
    /// <summary>エフェクト停止</summary>
    EffectStop,
    /// <summary>端末内蔵プログラム再生（A1コマンド）</summary>
    InternalProgram,
    // 2026-05-30 追加: 2色交互点灯 (A2)
    /// <summary>2色交互点灯（BPMで指定した周期で Color1↔Color2 を繰り返す）</summary>
    Color2,
}
