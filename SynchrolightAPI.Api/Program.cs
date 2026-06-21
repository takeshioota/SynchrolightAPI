using System.Text.Json;
using System.Text.Json.Serialization;
using SynchrolightAPI.Api.Services;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

var builder = WebApplication.CreateBuilder(args);

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

app.MapControllers();

app.Run();
