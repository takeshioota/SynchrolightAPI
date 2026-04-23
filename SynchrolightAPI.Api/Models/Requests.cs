namespace SynchrolightAPI.Api.Models;

// --- Transport ---
public record ConnectRequest(string[] PortNames);

// --- Transmitter ---
public record TransmitterInitRequest(int Channel, int Power);

// --- Light ---
public record GlobalColorRequest(ColorValue Color);

public record RowsRequest(int Field, int StartRow, int RowLen, ColorValue Color);

public record RowsEachRequest(int Field, int StartRow, int? Len, ColorValue? Color, ColorValue[]? Colors);

public record ColsRequest(int Field, int StartCol, int ColLen, ColorValue Color);

public record ColsEachRequest(int Field, int StartCol, int? Len, ColorValue? Color, ColorValue[]? Colors);

public record PointsRequest(int Field, int StartRow, int StartCol, int? Len, ColorValue? Color, ColorValue[]? Colors);

public record BlockRequest(int ProgNo, int BlockNo, ColorValue Color);

public record SequenceRequest(uint FrameNo);
