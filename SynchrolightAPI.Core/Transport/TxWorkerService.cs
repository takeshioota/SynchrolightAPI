using System.IO.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SynchrolightAPI.Transport;

/// <summary>
/// BackgroundService: 送信キューからDequeueして全接続ポートに同報送信
/// </summary>
public class TxWorkerService : BackgroundService
{
    private readonly MultiPortTransport _transport;
    private readonly ILogger<TxWorkerService> _logger;
    private readonly int _sendIntervalMs;

    public TxWorkerService(
        MultiPortTransport transport,
        ILogger<TxWorkerService> logger,
        IConfiguration configuration)
    {
        _transport = transport;
        _logger = logger;
        _sendIntervalMs = configuration.GetValue<int>("SerialPort:SendIntervalMs", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TxWorker開始 (送信間隔: {Interval}ms)", _sendIntervalMs);

        try
        {
            await foreach (var packet in _transport.Reader.ReadAllAsync(stoppingToken))
            {
                var ports = _transport.ConnectedPorts;
                foreach (var sp in ports)
                {
                    try
                    {
                        sp.Write(packet, 0, packet.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ポート {Port} への送信失敗", sp.PortName);
                        _transport.SetLastError($"{sp.PortName}: {ex.Message}");
                    }
                }

                if (_sendIntervalMs > 0)
                {
                    await Task.Delay(_sendIntervalMs, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TxWorker停止");
        }
    }
}
