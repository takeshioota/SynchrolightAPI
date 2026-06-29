using SynchrolightAPI.Services;
using SynchrolightAPI.Domain;

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

    // V4.5 追加: レインボー（CommandType=Rainbow 時のみ有効）
    /// <summary>レインボーモード（0=Solid/1=Blink/2=FadeInOut/3=FadeIn/4=FadeOut/5=Random）</summary>
    public int? RainbowMode { get; set; }

    /// <summary>レインボーカラーパレット（2〜7色）</summary>
    public List<Rgb>? RainbowColors { get; set; }

    /// <summary>色切り替え速度（ms）</summary>
    public int? RainbowCycleDurationMs { get; set; }

    /// <summary>点滅周期（ms）— Blink モード時のみ</summary>
    public int? RainbowBlinkPeriodMs { get; set; }

    /// <summary>点灯比率（1〜9）— Blink モード時のみ</summary>
    public int? RainbowDutyRatio { get; set; }

    /// <summary>フェードイン時間（ms）— FadeInOut/FadeIn モード時</summary>
    public int? RainbowFadeInMs { get; set; }

    /// <summary>フェードアウト時間（ms）— FadeInOut/FadeOut モード時</summary>
    public int? RainbowFadeOutMs { get; set; }
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
    // V4.5 追加: レインボー（0xA9） — シーケンスステップ対応
    /// <summary>レインボー開始（0xA9 0x02 色テーブル → 0xA9 0x03 モード継続送信）</summary>
    Rainbow,
    /// <summary>レインボー停止（連続送信中断）</summary>
    RainbowStop,
    /// <summary>レインボー一時停止（0xA9 0x04: 前回色を保持して継続送信）</summary>
    RainbowPause,
}
