using System.IO.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Diagnostics;
using SynchrolightAPI.Settings;

namespace SynchrolightAPI.Transport;

/// <summary>
/// BackgroundService: 2チャネル優先度制御 + ルーティング送信 + 再送制御
/// </summary>
public class TxWorkerService : BackgroundService
{
    private readonly MultiPortTransport _transport;
    private readonly ILogger<TxWorkerService> _logger;
    private readonly LatencyTracker? _latencyTracker;
    private readonly SettingsService? _settings;
    private readonly int _sendIntervalMs;

    public TxWorkerService(
        MultiPortTransport transport,
        ILogger<TxWorkerService> logger,
        IConfiguration configuration,
        LatencyTracker? latencyTracker = null,
        SettingsService? settings = null)
    {
        _transport = transport;
        _logger = logger;
        _latencyTracker = latencyTracker;
        _settings = settings;
        _sendIntervalMs = configuration.GetValue<int>("SerialPort:SendIntervalMs", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TxWorker開始 (送信間隔: {Interval}ms)", _sendIntervalMs);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SendEnvelope? envelope = null;

                // 高優先チャネルを先に消化
                if (_transport.HighPriorityReader.TryRead(out envelope))
                {
                    // high priority packet available
                }
                else if (_transport.NormalReader.TryRead(out envelope))
                {
                    // normal packet available
                }
                else
                {
                    // 両チャネル空 → いずれかにデータが来るまで待機
                    var highTask = _transport.HighPriorityReader.WaitToReadAsync(stoppingToken).AsTask();
                    var normalTask = _transport.NormalReader.WaitToReadAsync(stoppingToken).AsTask();
                    await Task.WhenAny(highTask, normalTask);
                    continue; // ループ先頭に戻ってTryReadで取得
                }

                // Deadline超過チェック
                if (envelope.IsExpired)
                {
                    _logger.LogWarning("Deadline超過パケット破棄 [{OpId}]", envelope.OperationId ?? "-");
                    continue;
                }

                await SendToTargetPorts(envelope);

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

    private async Task SendToTargetPorts(SendEnvelope envelope)
    {
        var allPorts = _transport.ConnectedPorts;
        var targetPorts = FilterPorts(allPorts, envelope.TargetPortNames);

        // 再送回数: コマンド単位のオーバーライド → グローバル設定 → デフォルト3回
        var retransmitCount = envelope.Options.RetransmitCount
            ?? _settings?.Current.RetransmitCount
            ?? 3;
        var retransmitIntervalMs = _settings?.Current.RetransmitIntervalMs ?? 5;

        for (int attempt = 0; attempt < retransmitCount; attempt++)
        {
            foreach (var sp in targetPorts)
            {
                try
                {
                    sp.Write(envelope.Packet, 0, envelope.Packet.Length);

                    if (attempt == 0)
                    {
                        var latencyMs = envelope.GetElapsedMs();
                        _latencyTracker?.Record(sp.PortName, latencyMs);

                        _logger.LogDebug("送信完了 [{OpId}] → {Port} ({Latency:F1}ms) 再送{Attempt}/{Total}",
                            envelope.OperationId ?? "-", sp.PortName, latencyMs, attempt + 1, retransmitCount);
                    }
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "ポート {Port} 切断検出 [{OpId}]", sp.PortName, envelope.OperationId ?? "-");
                    _transport.MarkPortDisconnected(sp.PortName);
                    _transport.SetLastError($"{sp.PortName}: disconnected - {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ポート {Port} への送信失敗 [{OpId}]", sp.PortName, envelope.OperationId ?? "-");
                    _transport.SetLastError($"{sp.PortName}: {ex.Message}");
                }
            }

            // 最終回は待機不要
            if (attempt < retransmitCount - 1 && retransmitIntervalMs > 0)
            {
                await Task.Delay(retransmitIntervalMs);
            }
        }
    }

    private static IReadOnlyList<SerialPort> FilterPorts(
        IReadOnlyList<SerialPort> allPorts,
        IReadOnlyList<string>? targetPortNames)
    {
        if (targetPortNames == null || targetPortNames.Count == 0)
            return allPorts; // 全ポート同報

        return allPorts
            .Where(p => targetPortNames.Contains(p.PortName, StringComparer.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }
}
