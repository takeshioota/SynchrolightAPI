using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Api.Logging;
using SynchrolightAPI.Api.Services;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

var builder = WebApplication.CreateBuilder(args);

// ===== ファイルログ（不具合解析用） =====
// appsettings.json の Logging:FileLog で ON/OFF・出力先・レベルを切替可能。
// 不具合時にこのファイルを確認／共有すれば原因究明がスムーズになる（既定 ON・Information）。
var fileLogEnabled = builder.Configuration.GetValue("Logging:FileLog:Enabled", true);
string fileLogDir = "(無効)";
var fileLogLevel = LogLevel.Information;
if (fileLogEnabled)
{
    var dir = builder.Configuration.GetValue<string>("Logging:FileLog:Directory") ?? "logs";
    fileLogDir = Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
    var lvl = builder.Configuration.GetValue<string>("Logging:FileLog:MinLevel") ?? "Information";
    if (!Enum.TryParse(lvl, ignoreCase: true, out fileLogLevel)) fileLogLevel = LogLevel.Information;
    var retainedDays = builder.Configuration.GetValue("Logging:FileLog:RetainedDays", 30);
    builder.Logging.AddProvider(new FileLoggerProvider(fileLogDir, fileLogLevel, retainedDays));
}

// JSON: camelCase + enum を文字列でシリアライズ
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// Core DI (API仕様書・設計書準拠)
builder.Services.AddSingleton<ICommandBuilder, CommandBuilder>();
builder.Services.AddSingleton<MultiPortTransport>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MultiPortTransport>>();
    int capacity = builder.Configuration.GetValue("SerialPort:QueueCapacity", 256);
    return new MultiPortTransport(logger, capacity);
});
// 送信ログ: リングバッファ + ITransport デコレータ
builder.Services.AddSingleton<SendLogStore>();
builder.Services.AddSingleton<ITransport>(sp =>
    new ApiLoggingTransportDecorator(
        sp.GetRequiredService<MultiPortTransport>(),
        sp.GetRequiredService<SendLogStore>()));
builder.Services.AddSingleton<LightingService>();
builder.Services.AddHostedService<TxWorkerService>();
// ポート死活監視（ケーブル抜け検知ハートビート + 自動再接続）。
// 5秒ごとに ReconcilePhysicalPorts() で USB 抜去ポートを切断としてマークする。
builder.Services.AddHostedService<PortHealthMonitor>();

// Effect / Sequence サービス
builder.Services.AddSingleton<InterpolationService>();
builder.Services.AddSingleton<EffectScheduler>();
builder.Services.AddSingleton<EffectEngine>();
builder.Services.AddSingleton<SequenceStore>();
builder.Services.AddSingleton<SequencePlayer>();
builder.Services.AddSingleton<SequenceRecorder>();
builder.Services.AddSingleton<EffectRunnerService>();

// BLE (Bluetooth Low Energy) サービス
builder.Services.AddSingleton<IBleTransport, BleTransport>();
builder.Services.AddSingleton<BleFileTransferService>();

var app = builder.Build();

// 起動バナー：共有されたログ単体から「いつ・どのビルド・どの設定で動いていたか」が分かるようにする。
app.Logger.LogInformation(
    "===== SynchrolightAPI 起動 ===== 時刻={Time:yyyy-MM-dd HH:mm:ss} / ファイルログ={FileLog} / 出力先={Dir} / レベル={Level} / exe={Exe}",
    DateTime.Now, fileLogEnabled, fileLogDir, fileLogLevel, Environment.ProcessPath);

app.MapControllers();

app.Run();
