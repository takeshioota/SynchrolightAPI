namespace SynchrolightAPI.Wpf.ViewModels;

public class MainViewModel
{
    public ConnectionViewModel Connection { get; }
    public TransmitterSettingsViewModel TransmitterSettings { get; }
    public CommandPanelViewModel CommandPanel { get; }
    public SendLogViewModel SendLog { get; }
    public BleIdPanelViewModel BleIdPanel { get; }

    public MainViewModel(
        ConnectionViewModel connection,
        TransmitterSettingsViewModel transmitterSettings,
        CommandPanelViewModel commandPanel,
        SendLogViewModel sendLog,
        BleIdPanelViewModel bleIdPanel)
    {
        Connection = connection;
        TransmitterSettings = transmitterSettings;
        CommandPanel = commandPanel;
        SendLog = sendLog;
        BleIdPanel = bleIdPanel;
    }
}
