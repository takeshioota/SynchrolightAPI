using CommunityToolkit.Mvvm.ComponentModel;

namespace SynchrolightAPI.Wpf.Models;

/// <summary>ゾーン設定のUI表示用モデル</summary>
public partial class ZoneItem : ObservableObject
{
    [ObservableProperty]
    private string _zoneId = "";

    [ObservableProperty]
    private string _portName = "";

    [ObservableProperty]
    private byte _txChannel = 1;

    [ObservableProperty]
    private byte _txPower;

    [ObservableProperty]
    private byte _field;
}
