using CommunityToolkit.Mvvm.ComponentModel;

namespace SynchrolightAPI.Wpf.Models;

public partial class BleDeviceItem : ObservableObject
{
    public ulong Address { get; init; }
    public string AddressHex => Address.ToString("X12");
    public string Name { get; init; } = "";
    public short Rssi { get; init; }

    [ObservableProperty] private int? _session;
    [ObservableProperty] private int? _row;
    [ObservableProperty] private int? _col;
    [ObservableProperty] private string _status = "";
}
