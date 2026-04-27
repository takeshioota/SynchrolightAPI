using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Transport;
using SynchrolightAPI.Wpf.Models;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class CommandPanelViewModel : ObservableObject
{
    private readonly ITransport _transport;
    private readonly SettingsService? _settings;

    public CommandType[] CommandTypes { get; } = Enum.GetValues<CommandType>();

    [ObservableProperty]
    private CommandType _selectedCommand = CommandType.A2_GlobalColor;

    // --- 共通パラメータ ---
    [ObservableProperty]
    private byte _field = 0x01;

    [ObservableProperty]
    private ushort _startRow = 1;

    [ObservableProperty]
    private ushort _startCol = 1;

    [ObservableProperty]
    private byte _len = 1;

    [ObservableProperty]
    private ushort _rowLen = 1;

    [ObservableProperty]
    private ushort _colLen = 1;

    // ShowRowLen用（AA_MultiRows）
    public bool ShowRowLen => SelectedCommand is CommandType.AA_MultiRows;

    // --- A1 ---
    [ObservableProperty]
    private uint _frameNo;

    // --- A6 ---
    [ObservableProperty]
    private byte _rxChannel = 1;

    // --- AC ---
    [ObservableProperty]
    private byte _progNo = 1;

    [ObservableProperty]
    private byte _blockNo = 1;

    // --- RGB ---
    [ObservableProperty]
    private byte _colorR = 0xFF;

    [ObservableProperty]
    private byte _colorG;

    [ObservableProperty]
    private byte _colorB;

    // --- ゾーン指定（Phase3） ---
    [ObservableProperty]
    private string? _targetZoneId;

    public string[] ZoneOptions { get; private set; } = ["(全体同報)"];

    // --- Visibility helpers ---
    public bool ShowField => SelectedCommand is CommandType.A2_GlobalColor or CommandType.A0_Points or CommandType.A3_Rows or CommandType.A4_Cols
        or CommandType.A6_SetRxChannel or CommandType.A8_MultiCols or CommandType.AA_MultiRows;
    public bool ShowStartRow => SelectedCommand is CommandType.A0_Points or CommandType.A3_Rows
        or CommandType.A6_SetRxChannel or CommandType.AA_MultiRows;
    public bool ShowStartCol => SelectedCommand is CommandType.A0_Points or CommandType.A4_Cols or CommandType.A8_MultiCols;
    public bool ShowLen => SelectedCommand is CommandType.A0_Points or CommandType.A3_Rows or CommandType.A4_Cols
        or CommandType.A6_SetRxChannel;
    public bool ShowColLen => SelectedCommand is CommandType.A8_MultiCols;
    public bool ShowFrameNo => SelectedCommand is CommandType.A1_PlaySequence;
    public bool ShowRxChannel => SelectedCommand is CommandType.A6_SetRxChannel;
    public bool ShowProgBlock => SelectedCommand is CommandType.AC_Block or CommandType.AE_BlockSector;
    public bool ShowRgb => SelectedCommand is not CommandType.A1_PlaySequence and not CommandType.A6_SetRxChannel;

    partial void OnSelectedCommandChanged(CommandType value)
    {
        OnPropertyChanged(nameof(ShowField));
        OnPropertyChanged(nameof(ShowStartRow));
        OnPropertyChanged(nameof(ShowStartCol));
        OnPropertyChanged(nameof(ShowLen));
        OnPropertyChanged(nameof(ShowRowLen));
        OnPropertyChanged(nameof(ShowColLen));
        OnPropertyChanged(nameof(ShowFrameNo));
        OnPropertyChanged(nameof(ShowRxChannel));
        OnPropertyChanged(nameof(ShowProgBlock));
        OnPropertyChanged(nameof(ShowRgb));
    }

    private readonly TransmitterSettingsViewModel? _transmitterSettings;

    public CommandPanelViewModel(
        ITransport transport,
        TransmitterSettingsViewModel? transmitterSettings = null,
        SettingsService? settings = null)
    {
        _transport = transport;
        _transmitterSettings = transmitterSettings;
        _settings = settings;

        // 保存済み色を復元
        if (_settings != null)
        {
            var lastColor = _settings.Current.LastColor;
            ColorR = lastColor.R;
            ColorG = lastColor.G;
            ColorB = lastColor.B;
        }

        // ゾーン選択肢を構築
        RefreshZoneOptions();
    }

    /// <summary>ゾーン設定からコンボボックス選択肢を更新</summary>
    public void RefreshZoneOptions()
    {
        var zones = _settings?.Current.Zones ?? [];
        var options = new List<string> { "(全体同報)" };
        foreach (var z in zones)
        {
            options.Add($"{z.ZoneId} ({z.PortName})");
        }
        ZoneOptions = options.ToArray();
        OnPropertyChanged(nameof(ZoneOptions));
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        byte[] packet = SelectedCommand switch
        {
            CommandType.A0_Points => LightProtocol.BuildA0_Points(
                Field, StartRow, StartCol, Len, CreateColorArray(Len)),
            CommandType.A1_PlaySequence => LightProtocol.BuildA1_PlaySequence(FrameNo),
            CommandType.A2_GlobalColor => LightProtocol.BuildA2_GlobalColor(Field, ColorR, ColorG, ColorB),
            CommandType.A3_Rows => LightProtocol.BuildA3_Rows(
                Field, StartRow, Len, ColorR, ColorG, ColorB),
            CommandType.A4_Cols => LightProtocol.BuildA4_Cols(
                Field, StartCol, Len, ColorR, ColorG, ColorB),
            CommandType.A6_SetRxChannel => LightProtocol.BuildA6_SetRxChannel(
                Field, StartRow, Len, RxChannel),
            CommandType.A8_MultiCols => LightProtocol.BuildA8_MultiColsSameColor(
                Field, StartCol, ColLen, ColorR, ColorG, ColorB),
            CommandType.AA_MultiRows => LightProtocol.BuildAA_MultiRowsSameColor(
                Field, StartRow, RowLen, ColorR, ColorG, ColorB),
            CommandType.AC_Block => LightProtocol.BuildAC_BlockColor(
                ProgNo, BlockNo, ColorR, ColorG, ColorB),
            CommandType.AE_BlockSector => LightProtocol.BuildAE_BlockColorSector(
                ProgNo, BlockNo, ColorR, ColorG, ColorB),
            _ => throw new InvalidOperationException()
        };

        // ゾーン指定付き送信
        var zoneId = ResolveZoneId();
        var options = zoneId != null
            ? SendOptions.Default with { TargetZoneId = zoneId }
            : SendOptions.Default;

        await _transport.EnqueueAsync(packet, options);
        _transmitterSettings?.UpdateLastSentPacket(packet);

        // 色を保存
        SaveLastColor();
    }

    [RelayCommand]
    private void SetPresetColor(string preset)
    {
        var rgb = preset switch
        {
            "Red" => Rgb.Red,
            "Green" => Rgb.Green,
            "Blue" => Rgb.Blue,
            "White" => Rgb.White,
            "Black" => Rgb.Black,
            _ => Rgb.Black
        };
        ColorR = rgb.R;
        ColorG = rgb.G;
        ColorB = rgb.B;
        SaveLastColor();
    }

    private void SaveLastColor()
    {
        _settings?.Update(s => s.LastColor = new RgbSetting(ColorR, ColorG, ColorB));
    }

    private string? ResolveZoneId()
    {
        if (string.IsNullOrEmpty(TargetZoneId) || TargetZoneId == "(全体同報)")
            return null;

        // "Zone1 (COM4)" → "Zone1"
        var spaceIdx = TargetZoneId.IndexOf(' ');
        return spaceIdx > 0 ? TargetZoneId[..spaceIdx] : TargetZoneId;
    }

    private (byte r, byte g, byte b)[] CreateColorArray(int len)
    {
        var arr = new (byte r, byte g, byte b)[len];
        Array.Fill(arr, (ColorR, ColorG, ColorB));
        return arr;
    }
}
