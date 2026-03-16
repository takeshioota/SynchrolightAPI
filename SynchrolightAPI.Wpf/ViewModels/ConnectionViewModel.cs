using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Transport;
using SynchrolightAPI.Wpf.Models;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private readonly ITransport _transport;

    public ObservableCollection<ComPortItem> Ports { get; } = [];

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "未接続";

    public ConnectionViewModel(ITransport transport)
    {
        _transport = transport;
        ScanPorts();
    }

    [RelayCommand]
    private void ScanPorts()
    {
        Ports.Clear();
        foreach (var name in SerialPort.GetPortNames().OrderBy(n => n))
        {
            Ports.Add(new ComPortItem(name, true));
        }
        if (Ports.Count == 0)
            StatusText = "COMポートなし";
        else
            StatusText = "未接続";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var selected = Ports.Where(p => p.IsSelected).Select(p => p.Name).ToList();
        if (selected.Count == 0) return;

        await _transport.ConnectAsync(selected);
        var status = _transport.GetStatus();
        IsConnected = status.ConnectedPorts > 0;
        StatusText = IsConnected
            ? $"接続済 ({status.ConnectedPorts})"
            : "接続失敗";
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _transport.DisconnectAsync();
        IsConnected = false;
        StatusText = "未接続";
    }
}
