using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Wpf.Models;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class ZoneSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly CommandPanelViewModel? _commandPanel;

    public ObservableCollection<ZoneItem> Zones { get; } = [];

    [ObservableProperty]
    private ZoneItem? _selectedZone;

    [ObservableProperty]
    private string _statusText = "";

    public ZoneSettingsViewModel(
        SettingsService settings,
        CommandPanelViewModel? commandPanel = null)
    {
        _settings = settings;
        _commandPanel = commandPanel;

        // 保存済みゾーン設定を復元
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        Zones.Clear();
        foreach (var z in _settings.Current.Zones)
        {
            Zones.Add(new ZoneItem
            {
                ZoneId = z.ZoneId,
                PortName = z.PortName,
                TxChannel = z.TxChannel,
                TxPower = z.TxPower
            });
        }
    }

    [RelayCommand]
    private void AddZone()
    {
        var nextId = Zones.Count + 1;
        Zones.Add(new ZoneItem
        {
            ZoneId = $"Zone{nextId}",
            PortName = "",
            TxChannel = (byte)nextId,
            TxPower = 0,
            Field = (byte)nextId
        });
    }

    [RelayCommand]
    private void RemoveZone()
    {
        if (SelectedZone != null)
        {
            Zones.Remove(SelectedZone);
            SelectedZone = null;
        }
    }

    [RelayCommand]
    private void ApplyZones()
    {
        // 設定を永続化
        _settings.Update(s =>
        {
            s.Zones = Zones.Select(z =>
                new ZoneSettingEntry(z.ZoneId, z.PortName, z.TxChannel, z.TxPower)).ToList();
        });

        // CommandPanelのゾーン選択肢を更新
        _commandPanel?.RefreshZoneOptions();

        StatusText = $"ゾーン設定を保存しました ({Zones.Count}ゾーン)";
    }
}
