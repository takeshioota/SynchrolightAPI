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
public record RxChannelRequest(int Field, int StartRow, int Len, int Channel);
public record BlockSectorRequest(int ProgNo, int BlockNo, ColorValue Color);

// --- Transport (追加) ---
public record KeepAliveRequest(string? Base64Packet);
