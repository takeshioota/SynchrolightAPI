using CommunityToolkit.Mvvm.ComponentModel;

namespace SynchrolightAPI.Wpf.Models;

public partial class ComPortItem : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ComPortItem(string name, bool isSelected = false)
    {
        Name = name;
        _isSelected = isSelected;
    }
}
