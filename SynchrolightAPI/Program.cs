using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Diagnostics;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Transport;

var builder = Host.CreateApplicationBuilder(args);

// モード選択: --mock で実機不要テスト、--diag で診断、デフォルトで通常テスト
var mode = args.Length > 0 ? args[0] : "--mock";

switch (mode)
{
    case "--mock":
        // Phase2/Phase3 統合テスト（MemoryTransport使用、実機不要）
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<LatencyTracker>();
        builder.Services.AddSingleton<ZoneRouter>();
        builder.Services.AddSingleton<ICommandBuilder, CommandBuilder>();

        builder.Services.AddSingleton<MemoryTransport>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<MemoryTransport>>();
            var capacity = sp.GetRequiredService<IConfiguration>().GetValue<int>("SerialPort:QueueCapacity", 256);
            var zoneRouter = sp.GetRequiredService<ZoneRouter>();
            return new MemoryTransport(logger, capacity, zoneRouter);
        });
        builder.Services.AddSingleton<ITransport>(sp => sp.GetRequiredService<MemoryTransport>());

        builder.Services.AddSingleton<CommandThrottler>(sp =>
            new CommandThrottler(
                sp.GetRequiredService<ITransport>(),
                sp.GetRequiredService<ILogger<CommandThrottler>>()));

        builder.Services.AddSingleton<LightingService>(sp =>
            new LightingService(
                sp.GetRequiredService<ICommandBuilder>(),
                sp.GetRequiredService<ITransport>(),
                sp.GetRequiredService<ILogger<LightingService>>(),
                sp.GetRequiredService<CommandThrottler>()));

        builder.Services.AddHostedService<MemoryTxWorkerService>(sp =>
            new MemoryTxWorkerService(
                sp.GetRequiredService<MemoryTransport>(),
                sp.GetRequiredService<ILogger<MemoryTxWorkerService>>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<LatencyTracker>()));

        builder.Services.AddHostedService<Phase2Phase3TestService>();
        break;

    case "--diag":
        // 診断モード: 全ポート×全チャンネル総当たりテスト（実機必要）
        builder.Services.AddHostedService<DiagnosticService>();
        break;

    default:
        // 通常モード: テストシナリオ実行（実機必要）
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<LatencyTracker>();
        builder.Services.AddSingleton<ZoneRouter>();
        builder.Services.AddSingleton<ICommandBuilder, CommandBuilder>();
        builder.Services.AddSingleton<MultiPortTransport>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<MultiPortTransport>>();
            var capacity = sp.GetRequiredService<IConfiguration>().GetValue<int>("SerialPort:QueueCapacity", 256);
            var zoneRouter = sp.GetRequiredService<ZoneRouter>();
            return new MultiPortTransport(logger, capacity, zoneRouter);
        });
        builder.Services.AddSingleton<ITransport>(sp => sp.GetRequiredService<MultiPortTransport>());
        builder.Services.AddSingleton<LightingService>();
        builder.Services.AddHostedService<TxWorkerService>(sp =>
            new TxWorkerService(
                sp.GetRequiredService<MultiPortTransport>(),
                sp.GetRequiredService<ILogger<TxWorkerService>>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<LatencyTracker>()));
        builder.Services.AddHostedService<PortHealthMonitor>();
        builder.Services.AddHostedService<TestScenarioService>();
        break;
}

var host = builder.Build();
await host.RunAsync();
