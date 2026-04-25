using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Diagnostics;

namespace SynchrolightAPI.Transport;

/// <summary>
/// MemoryTransport用の送信ワーカー。
/// TxWorkerServiceと同じ2チャネル優先度制御ロジックを持つが、
/// SerialPortの代わりに仮想ポートへのログ出力で動作する。
/// </summary>
public class MemoryTxWorkerService : BackgroundService
{
    private readonly MemoryTransport _transport;
    private readonly ILogger<MemoryTxWorkerService> _logger;
    private readonly LatencyTracker? _latencyTracker;
    private readonly int _sendIntervalMs;

    public MemoryTxWorkerService(
        MemoryTransport transport,
        ILogger<MemoryTxWorkerService> logger,
        IConfiguration configuration,
        LatencyTracker? latencyTracker = null)
    {
        _transport = transport;
        _logger = logger;
        _latencyTracker = latencyTracker;
        _sendIntervalMs = configuration.GetValue<int>("SerialPort:SendIntervalMs", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[MOCK] TxWorker開始 (送信間隔: {Interval}ms)", _sendIntervalMs);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SendEnvelope? envelope = null;

                if (_transport.HighPriorityReader.TryRead(out envelope))
                {
                    // high priority
                }
                else if (_transport.NormalReader.TryRead(out envelope))
                {
                    // normal
                }
                else
                {
                    var highTask = _transport.HighPriorityReader.WaitToReadAsync(stoppingToken).AsTask();
                    var normalTask = _transport.NormalReader.WaitToReadAsync(stoppingToken).AsTask();
                    await Task.WhenAny(highTask, normalTask);
                    continue;
                }

                // Deadline超過チェック
                if (envelope.IsExpired)
                {
                    _logger.LogWarning("[MOCK] Deadline超過パケット破棄 [{OpId}]", envelope.OperationId ?? "-");
                    continue;
                }

                // 仮想ポートへの送信
                var ports = _transport.ConnectedVirtualPorts;
                var targetPorts = FilterPorts(ports, envelope.TargetPortNames);

                foreach (var port in targetPorts)
                {
                    _transport.SimulateSend(envelope, port);

                    var latencyMs = envelope.GetElapsedMs();
                    _latencyTracker?.Record(port.Name, latencyMs);
                }

                if (_sendIntervalMs > 0)
                {
                    await Task.Delay(_sendIntervalMs, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[MOCK] TxWorker停止");
        }
    }

    private static IReadOnlyList<VirtualPort> FilterPorts(
        IReadOnlyList<VirtualPort> allPorts,
        IReadOnlyList<string>? targetPortNames)
    {
        if (targetPortNames == null || targetPortNames.Count == 0)
            return allPorts;

        return allPorts
            .Where(p => targetPortNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }
}
