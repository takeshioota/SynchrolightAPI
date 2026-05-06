using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Wpf.Models;
using SynchrolightAPI.Wpf.Services;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class CommandPanelViewModel : ObservableObject
{
    private readonly SettingsService? _settings;
    private readonly SynchrolightApiClient? _apiClient;
    private bool _isEffectRunning;
    private CancellationTokenSource? _colorDebounceCts;

    // --- モード切替 ---
    [ObservableProperty]
    private bool _isEffectMode;

    public CommandType[] CommandTypes { get; } = Enum.GetValues<CommandType>();

    // コマンドトグルボタン用
    public CommandItem[] CommandItems { get; }

    [ObservableProperty]
    private CommandType _selectedCommand = CommandType.A2_GlobalColor;

    // --- 共通パラメータ ---
    [ObservableProperty]
    private byte _field = 0x00;

    // Field ComboBox用
    public FieldOption[] FieldOptions { get; } =
    [
        new(0x00, "00", "ID未書込み（ブロードキャスト）"),
        new(0x01, "01", "ID書込み済み端末"),
    ];

    [ObservableProperty]
    private FieldOption _selectedFieldOption;

    [ObservableProperty]
    private string _fieldDescription = "ID未書込み（ブロードキャスト）";

    partial void OnSelectedFieldOptionChanged(FieldOption value)
    {
        if (value == null) return;
        Field = value.Value;
        FieldDescription = value.Description;
    }

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

    partial void OnColorRChanged(byte value) => RestartEffectDebounced();
    partial void OnColorGChanged(byte value) => RestartEffectDebounced();
    partial void OnColorBChanged(byte value) => RestartEffectDebounced();

    private void RestartEffectDebounced()
    {
        if (!_isEffectRunning) return;

        // プリセットカラー等でR/G/Bが連続変更されるとき、最後の1回だけ再起動
        _colorDebounceCts?.Cancel();
        _colorDebounceCts = new CancellationTokenSource();
        var token = _colorDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token);
                if (!token.IsCancellationRequested)
                    App.Current?.Dispatcher.Invoke(() => _ = StartEffectAsync());
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    // --- エフェクト ---
    public EffectType[] EffectTypes { get; } = Enum.GetValues<EffectType>();
    public EffectItem[] EffectItems { get; }

    [ObservableProperty]
    private EffectType _selectedEffect = EffectType.Flash;

    partial void OnSelectedEffectChanged(EffectType value)
    {
        // エフェクト実行中なら新しいエフェクトで即座に再起動
        if (_isEffectRunning)
        {
            _ = StartEffectAsync();
        }
    }

    public static int CycleDurationMin => 100;
    public static int CycleDurationMax => 15000;

    [ObservableProperty]
    private int _selectedCycleDuration = 1000;

    public string CycleDurationDisplay => SelectedCycleDuration >= 1000
        ? $"{SelectedCycleDuration / 1000.0:0.#}秒"
        : $"{SelectedCycleDuration}ms";

    partial void OnSelectedCycleDurationChanged(int value)
    {
        OnPropertyChanged(nameof(CycleDurationDisplay));
        // エフェクト実行中なら新しい速度で即座に再起動
        if (_isEffectRunning)
        {
            _ = StartEffectAsync();
        }
    }

    [ObservableProperty]
    private string _effectStatus = "";

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
        SynchrolightApiClient? apiClient = null,
        TransmitterSettingsViewModel? transmitterSettings = null,
        SettingsService? settings = null)
    {
        _apiClient = apiClient;
        _transmitterSettings = transmitterSettings;
        _settings = settings;

        // トグルボタン初期化
        CommandItems = BuildCommandItems();
        EffectItems = BuildEffectItems();

        // Field初期値
        _selectedFieldOption = FieldOptions[0]; // 00

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
        if (_apiClient == null) return;

        // キープアライブ用に最後のパケットを記録
        byte[]? packetForKeepAlive = SelectedCommand switch
        {
            CommandType.A2_GlobalColor => LightProtocol.BuildA2_GlobalColor(Field, ColorR, ColorG, ColorB),
            CommandType.A3_Rows => LightProtocol.BuildA3_Rows(Field, StartRow, Len, ColorR, ColorG, ColorB),
            _ => null
        };

        switch (SelectedCommand)
        {
            case CommandType.A0_Points:
                await _apiClient.SetPointsAsync(Field, StartRow, StartCol, Len, ColorR, ColorG, ColorB);
                break;
            case CommandType.A1_PlaySequence:
                await _apiClient.PlayHwSequenceAsync(FrameNo);
                break;
            case CommandType.A2_GlobalColor:
                await _apiClient.SetGlobalColorAsync(Field, ColorR, ColorG, ColorB);
                break;
            case CommandType.A3_Rows:
                await _apiClient.SetRowsEachAsync(Field, StartRow, Len, ColorR, ColorG, ColorB);
                break;
            case CommandType.A4_Cols:
                await _apiClient.SetColsEachAsync(Field, StartCol, Len, ColorR, ColorG, ColorB);
                break;
            case CommandType.A6_SetRxChannel:
                await _apiClient.SetRxChannelAsync(Field, StartRow, Len, RxChannel);
                break;
            case CommandType.A8_MultiCols:
                await _apiClient.SetMultiColsAsync(Field, StartCol, ColLen, ColorR, ColorG, ColorB);
                break;
            case CommandType.AA_MultiRows:
                await _apiClient.SetMultiRowsAsync(Field, StartRow, RowLen, ColorR, ColorG, ColorB);
                break;
            case CommandType.AC_Block:
                await _apiClient.SetBlockColorAsync(ProgNo, BlockNo, ColorR, ColorG, ColorB);
                break;
            case CommandType.AE_BlockSector:
                await _apiClient.SetBlockSectorAsync(ProgNo, BlockNo, ColorR, ColorG, ColorB);
                break;
        }

        if (packetForKeepAlive != null)
            _transmitterSettings?.UpdateLastSentPacket(packetForKeepAlive);

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

    [RelayCommand]
    private async Task StartEffectAsync()
    {
        if (_apiClient == null) return;

        // 色が黒(0,0,0)の場合は赤にフォールバック（黒↔黒は視認不可）
        if (ColorR == 0 && ColorG == 0 && ColorB == 0)
        {
            ColorR = 0xFF;
            ColorG = 0x00;
            ColorB = 0x00;
        }

        var ok = await _apiClient.StartEffectAsync(
            SelectedEffect, ColorR, ColorG, ColorB,
            field: Field,
            cycleDurationMs: SelectedCycleDuration,
            flashIntervalMs: SelectedCycleDuration / 2,
            continuous: true);

        if (ok)
        {
            _isEffectRunning = true;
            EffectStatus = $"{SelectedEffect} 実行中...";
        }
    }

    [RelayCommand]
    private async Task StopEffectAsync()
    {
        if (_apiClient != null)
        {
            await _apiClient.StopEffectAsync();
            await _apiClient.AllOffAsync();
        }

        _isEffectRunning = false;
        EffectStatus = "";
    }

    private CommandItem[] BuildCommandItems()
    {
        var items = new (CommandType type, string code, string label, string icon)[]
        {
            (CommandType.A2_GlobalColor,  "A2", "全体",     "\U0001F30D"),
            (CommandType.A0_Points,       "A0", "座標",     "\U0001F4CD"),
            (CommandType.A3_Rows,         "A3", "行",       "\u2B1C"),
            (CommandType.A4_Cols,         "A4", "列",       "\u25FD"),
            (CommandType.A8_MultiCols,    "A8", "複列",     "\u25AB"),
            (CommandType.AA_MultiRows,    "AA", "複行",     "\u25AA"),
            (CommandType.AC_Block,        "AC", "ブロック", "\U0001F532"),
            (CommandType.AE_BlockSector,  "AE", "セクタ",   "\U0001F533"),
            (CommandType.A1_PlaySequence, "A1", "再生",     "\u25B6"),
            (CommandType.A6_SetRxChannel, "A6", "RxCH",    "\U0001F4E1"),
        };

        return items.Select(i =>
        {
            var item = new CommandItem(i.type, i.code, i.label, i.icon, i.type == SelectedCommand);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CommandItem.IsSelected) && item.IsSelected)
                    SelectedCommand = item.Type;
            };
            return item;
        }).ToArray();
    }

    private EffectItem[] BuildEffectItems()
    {
        var items = new (EffectType type, string icon, string label)[]
        {
            (EffectType.Flash,      "\u26A1", "Flash"),
            (EffectType.FadeIn,     "\U0001F305", "Fade IN"),
            (EffectType.FadeOut,    "\U0001F307", "Fade OUT"),
            (EffectType.Breathing,  "\U0001F4A8", "Breathing"),
            (EffectType.SevenColor, "\U0001F308", "7 Color"),
        };

        return items.Select(i =>
        {
            var item = new EffectItem(i.type, i.icon, i.label, i.type == SelectedEffect);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EffectItem.IsSelected) && item.IsSelected)
                    SelectedEffect = item.Type;
            };
            return item;
        }).ToArray();
    }
}

/// <summary>エフェクトトグルボタン用アイテム</summary>
public partial class EffectItem : ObservableObject
{
    public EffectType Type { get; }
    public string Icon { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public EffectItem(EffectType type, string icon, string label, bool isSelected)
    {
        Type = type;
        Icon = icon;
        Label = label;
        _isSelected = isSelected;
    }
}

/// <summary>コマンドトグルボタン用アイテム</summary>
public partial class CommandItem : ObservableObject
{
    public CommandType Type { get; }
    public string Code { get; }
    public string Label { get; }
    public string Icon { get; }

    [ObservableProperty]
    private bool _isSelected;

    public CommandItem(CommandType type, string code, string label, string icon, bool isSelected)
    {
        Type = type;
        Code = code;
        Label = label;
        Icon = icon;
        _isSelected = isSelected;
    }
}
