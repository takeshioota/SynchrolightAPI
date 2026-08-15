namespace SynchrolightAPI.Api.Models;

// --- Transport ---
public record ConnectRequest(string[] PortNames);

// --- Transmitter ---
public record TransmitterInitRequest(int Channel, int Power);

// --- Light ---
public record GlobalColorRequest(ColorValue Color, int? Field = null);

public record RowsRequest(int Field, int StartRow, int RowLen, ColorValue Color);

public record RowsEachRequest(int Field, int StartRow, int? Len, ColorValue? Color, ColorValue[]? Colors);

public record ColsRequest(int Field, int StartCol, int ColLen, ColorValue Color);

public record ColsEachRequest(int Field, int StartCol, int? Len, ColorValue? Color, ColorValue[]? Colors);

public record PointsRequest(int Field, int StartRow, int StartCol, int? Len, ColorValue? Color, ColorValue[]? Colors);

public record BlockRequest(int ProgNo, int BlockNo, ColorValue Color);

public record SequenceRequest(uint FrameNo);

// --- Effect ---
public record StartEffectRequest(
    SynchrolightAPI.Services.EffectType Type,
    ColorValue Color,
    int? Field,
    int? CycleDurationMs,
    int? FlashIntervalMs,
    int? FadeSteps,
    bool? Continuous
);

// 色→色のスムーズ遷移（サーバ側で補間送信）。UI/シーケンス共通の堅牢フェード経路。
public record FadeRequest(
    ColorValue From,
    ColorValue To,
    int DurationMs,
    int? FadeSteps = null,
    int? Field = null
);

// --- Sequence ---
public record PlaySequenceRequest(string Name);

public record SaveSequenceRequest(string Name, List<SynchrolightAPI.Models.SequenceStep> Steps);
public record PlayInlineRequest(List<SynchrolightAPI.Models.SequenceStep> Steps, bool? Loop);
public record PlayStepRequest(SynchrolightAPI.Models.SequenceStep Step);
public record JumpToStepRequest(int StepIndex);

// --- Transmitter (追加) ---
public record SetChannelRequest(int Channel);
public record SetPowerRequest(int Power);

// --- Light (追加) ---
// 受信端チャンネル設定（A6/AD）は全体ブロードキャスト固定のため ch のみ受け取る（周波数変更 3.6/3.7 訂正仕様）
public record RxChannelRequest(int Channel);
public record RxChannelColRequest(int Channel);
public record BlockSectorRequest(int ProgNo, int BlockNo, ColorValue Color);

// --- Internal Program (SNO夏版) ---
public record InternalProgramRequest(uint FrameNo);

// --- Rainbow (V4.5: 3.15-3.22) ---
public record SendColorTableRequest(
    ColorValue[] Colors,       // カラーパレット（2〜7色）
    int CycleDurationMs = 1000 // 色切り替え速度（ms）— パレット送信後の反映待ち用
);

public record StartRainbowRequest(
    int Mode,                  // 0=Solid, 1=Blink, 2=FadeInOut, 3=FadeIn, 4=FadeOut, 5=Random
    ColorValue[] Colors,       // カラーパレット（2〜7色）
    int CycleDurationMs,       // 色切り替え速度（ms）
    int? BlinkPeriodMs,        // 点滅周期 — Blink モード時のみ
    int? DutyRatio,            // 点灯比率（1-9） — Blink モード時のみ
    int? FadeInMs,             // FI 時間 — FadeInOut/FadeIn モード時
    int? FadeOutMs             // FO 時間 — FadeInOut/FadeOut モード時
);

// --- Transport (追加) ---
public record KeepAliveRequest(string? Base64Packet);

// --- File Write (2.4GHz: 3.13-3.14) ---
public record FileWrite24GRequest(
    uint FrameNo,              // 書き込み先フレーム番号
    string Data                // Base64エンコードされたRGBデータ
);
