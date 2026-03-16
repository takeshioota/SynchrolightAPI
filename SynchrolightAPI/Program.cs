using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

var builder = Host.CreateApplicationBuilder(args);

// 診断モード: 全ポート×全チャンネル総当たりテスト
builder.Services.AddHostedService<DiagnosticService>();

// // 通常モード: テストシナリオ実行
// builder.Services.AddSingleton<ICommandBuilder, CommandBuilder>();
// builder.Services.AddSingleton<MultiPortTransport>(sp =>
// {
//     var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MultiPortTransport>>();
//     var capacity = sp.GetRequiredService<IConfiguration>().GetValue<int>("SerialPort:QueueCapacity", 256);
//     return new MultiPortTransport(logger, capacity);
// });
// builder.Services.AddSingleton<ITransport>(sp => sp.GetRequiredService<MultiPortTransport>());
// builder.Services.AddSingleton<LightingService>();
// builder.Services.AddHostedService<TxWorkerService>();
// builder.Services.AddHostedService<TestScenarioService>();

var host = builder.Build();
await host.RunAsync();
