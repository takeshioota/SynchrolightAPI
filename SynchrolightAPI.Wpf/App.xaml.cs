using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;
using SynchrolightAPI.Wpf.Services;
using SynchrolightAPI.Wpf.ViewModels;

namespace SynchrolightAPI.Wpf;

public partial class App : Application
{
    private IHost _host = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((ctx, services) =>
            {
                // Protocol層
                services.AddSingleton<ICommandBuilder, CommandBuilder>();

                // Transport層
                services.AddSingleton<MultiPortTransport>(sp =>
                {
                    var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MultiPortTransport>>();
                    var capacity = ctx.Configuration.GetValue<int>("SerialPort:QueueCapacity", 256);
                    return new MultiPortTransport(logger, capacity);
                });
                services.AddSingleton<ITransport>(sp =>
                {
                    var mpt = sp.GetRequiredService<MultiPortTransport>();
                    var logVm = sp.GetRequiredService<SendLogViewModel>();
                    return new LoggingTransportDecorator(mpt, logVm);
                });

                // Service層
                services.AddSingleton<LightingService>();

                // BackgroundServices
                services.AddHostedService<TxWorkerService>();

                // ViewModels
                services.AddSingleton<SendLogViewModel>();
                services.AddSingleton<ConnectionViewModel>();
                services.AddSingleton<TransmitterSettingsViewModel>();
                services.AddSingleton<CommandPanelViewModel>(sp =>
                    new CommandPanelViewModel(
                        sp.GetRequiredService<ITransport>(),
                        sp.GetRequiredService<TransmitterSettingsViewModel>()));
                services.AddSingleton<MainViewModel>();
            })
            .Build();

        await _host.StartAsync();

        var mainVm = _host.Services.GetRequiredService<MainViewModel>();
        var mainWindow = new Views.MainWindow { DataContext = mainVm };
        mainWindow.Show();
        MainWindow = mainWindow;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
