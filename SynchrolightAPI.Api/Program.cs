using System.Text.Json;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

var builder = WebApplication.CreateBuilder(args);

// JSON: camelCase
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

// Core DI (API仕様書・設計書準拠)
builder.Services.AddSingleton<ICommandBuilder, CommandBuilder>();
builder.Services.AddSingleton<MultiPortTransport>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MultiPortTransport>>();
    int capacity = builder.Configuration.GetValue("SerialPort:QueueCapacity", 256);
    return new MultiPortTransport(logger, capacity);
});
builder.Services.AddSingleton<ITransport>(sp => sp.GetRequiredService<MultiPortTransport>());
builder.Services.AddSingleton<LightingService>();
builder.Services.AddHostedService<TxWorkerService>();

var app = builder.Build();

app.MapControllers();

app.Run();
