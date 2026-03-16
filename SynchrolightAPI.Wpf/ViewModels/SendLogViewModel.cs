using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Wpf.Models;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class SendLogViewModel : ObservableObject
{
    private const int MaxLogEntries = 500;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void AddEntry(string direction, string hex)
    {
        var entry = new LogEntry(DateTime.Now, direction, hex);

        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            AppendEntry(entry);
        }
        else
        {
            Application.Current?.Dispatcher.BeginInvoke(() => AppendEntry(entry));
        }
    }

    private void AppendEntry(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxLogEntries)
            Entries.RemoveAt(0);
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
    }
}
