using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Wpf.Models;
using SynchrolightAPI.Wpf.Services;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private readonly SynchrolightApiClient _apiClient;
    private readonly SettingsService _settings;

    public ObservableCollection<ComPortItem> Ports { get; } = [];

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "未接続";

    public ConnectionViewModel(SynchrolightApiClient apiClient, SettingsService settings)
    {
        _apiClient = apiClient;
        _settings = settings;
        _ = ScanPortsAsync();
    }

    [RelayCommand]
    private async Task ScanPortsAsync()
    {
        try
        {
            var savedPorts = _settings.Current.SelectedPorts;
            var portNames = await _apiClient.ScanPortsAsync();
            Ports.Clear();
            foreach (var name in portNames)
            {
                var isSelected = savedPorts.Contains(name, StringComparer.OrdinalIgnoreCase);
                Ports.Add(new ComPortItem(name, isSelected));
            }
            StatusText = Ports.Count == 0 ? "COMポートなし" : "未接続";
        }
        catch (HttpRequestException)
        {
            StatusText = "API接続エラー";
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var selected = Ports.Where(p => p.IsSelected).Select(p => p.Name).ToList();
        if (selected.Count == 0) return;

        // 選択ポートを保存
        _settings.Update(s => s.SelectedPorts = selected);

        try
        {
            await _apiClient.ConnectAsync(selected);
            var status = await _apiClient.GetTransportStatusAsync();
            IsConnected = status.ConnectedPorts > 0;
            StatusText = IsConnected
                ? $"接続済 ({status.ConnectedPorts})"
                : "接続失敗";
        }
        catch (HttpRequestException)
        {
            StatusText = "API接続エラー";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await _apiClient.DisconnectAsync();
        }
        catch (HttpRequestException) { }
        IsConnected = false;
        StatusText = "未接続";
    }
}
