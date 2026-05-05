using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Diagnostics;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Transport;
using SynchrolightAPI.Wpf.Services;
using SynchrolightAPI.Wpf.ViewModels;

namespace SynchrolightAPI.Wpf;

public partial class App : Application
{
    private IHost _host = null!;
    private SettingsService? _settingsService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((ctx, services) =>
            {
                // 設定永続化
                services.AddSingleton<SettingsService>();

                // Protocol層
                services.AddSingleton<ICommandBuilder, CommandBuilder>();

                // Diagnostics
                services.AddSingleton<LatencyTracker>();

                // Zone Router
                services.AddSingleton<ZoneRouter>();

                // Transport層
                services.AddSingleton<MultiPortTransport>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<MultiPortTransport>>();
                    var capacity = ctx.Configuration.GetValue<int>("SerialPort:QueueCapacity", 256);
                    var zoneRouter = sp.GetRequiredService<ZoneRouter>();
                    return new MultiPortTransport(logger, capacity, zoneRouter);
                });
                services.AddSingleton<ITransport>(sp =>
                {
                    var mpt = sp.GetRequiredService<MultiPortTransport>();
                    var logVm = sp.GetRequiredService<SendLogViewModel>();
                    return new LoggingTransportDecorator(mpt, logVm);
                });

                // Command Throttler
                services.AddSingleton<CommandThrottler>(sp =>
                    new CommandThrottler(
                        sp.GetRequiredService<ITransport>(),
                        sp.GetRequiredService<ILogger<CommandThrottler>>()));

                // Service層
                services.AddSingleton<LightingService>(sp =>
                    new LightingService(
                        sp.GetRequiredService<ICommandBuilder>(),
                        sp.GetRequiredService<ITransport>(),
                        sp.GetRequiredService<ILogger<LightingService>>(),
                        sp.GetRequiredService<CommandThrottler>()));
                services.AddSingleton<InterpolationService>();
                services.AddSingleton<EffectScheduler>();
                services.AddSingleton<EffectEngine>();
                services.AddSingleton<SequenceStore>();
                services.AddSingleton<SequencePlayer>();

                // BackgroundServices
                services.AddHostedService<TxWorkerService>(sp =>
                    new TxWorkerService(
                        sp.GetRequiredService<MultiPortTransport>(),
                        sp.GetRequiredService<ILogger<TxWorkerService>>(),
                        sp.GetRequiredService<IConfiguration>(),
                        sp.GetRequiredService<LatencyTracker>(),
                        sp.GetRequiredService<SettingsService>()));
                services.AddHostedService<PortHealthMonitor>();

                // BLE
                services.AddSingleton<BleIdService>();

                // ViewModels
                services.AddSingleton<SendLogViewModel>();
                services.AddSingleton<ConnectionViewModel>(sp =>
                    new ConnectionViewModel(
                        sp.GetRequiredService<ITransport>(),
                        sp.GetRequiredService<SettingsService>()));
                services.AddSingleton<TransmitterSettingsViewModel>(sp =>
                    new TransmitterSettingsViewModel(
                        sp.GetRequiredService<LightingService>(),
                        sp.GetRequiredService<ITransport>(),
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<LatencyTracker>()));
                services.AddSingleton<CommandPanelViewModel>(sp =>
                    new CommandPanelViewModel(
                        sp.GetRequiredService<ITransport>(),
                        sp.GetRequiredService<TransmitterSettingsViewModel>(),
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<EffectEngine>(),
                        sp.GetRequiredService<EffectScheduler>()));
                services.AddSingleton<BleIdPanelViewModel>();
                services.AddSingleton<SequencePanelViewModel>();
                services.AddSingleton<ZoneSettingsViewModel>(sp =>
                    new ZoneSettingsViewModel(
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<ZoneRouter>(),
                        sp.GetRequiredService<CommandPanelViewModel>()));
                services.AddSingleton<MainViewModel>();
            })
            .Build();

        _settingsService = _host.Services.GetRequiredService<SettingsService>();

        await _host.StartAsync();

        var mainVm = _host.Services.GetRequiredService<MainViewModel>();
        var mainWindow = new Views.MainWindow { DataContext = mainVm };
        mainWindow.Show();
        MainWindow = mainWindow;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // 終了時に設定を保存
        _settingsService?.Save();

        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
