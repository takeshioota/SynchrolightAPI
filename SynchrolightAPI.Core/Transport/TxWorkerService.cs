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

    // ── 送信診断サマリ（BUG-20260928-01 切り分け: PC律速か送信機/シリアル律速か）──
    // Fade/Breathing で「演出が止まる」事象の切り分けに必要な
    //   ・キュー滞留（通常/高優先）… 滞留が常時高いなら消化<生産＝渋滞
    //   ・実ドレインレート（実際の送信/秒）… 生産(約50/s)を下回っていないか
    //   ・enqueue→送信の遅延 … 滞留で古いフレームが遅れて出ていないか
    //   ・Write 所要時間 … 小さいのに滞留大なら PC 側(送信間隔待ち等)律速、
    //                      大きいなら送信機/シリアル律速（この場合ドレイン高速化では直らない）
    // を INFO 級で定期的に残す（Debug に依存せず切り分け可能にする）。
    private const int DiagSummaryIntervalMs = 2000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TxWorker開始 (送信間隔: {Interval}ms)", _sendIntervalMs);

        var summarySw = System.Diagnostics.Stopwatch.StartNew();
        long sentInWindow = 0, droppedInWindow = 0;
        double sumLatencyMs = 0, maxLatencyMs = 0;
        double sumWriteMs = 0, maxWriteMs = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 診断サマリ（約2秒毎・直近ウィンドウに送信/破棄があった時のみ）
                if (summarySw.ElapsedMilliseconds >= DiagSummaryIntervalMs &&
                    (sentInWindow > 0 || droppedInWindow > 0))
                {
                    var sec = summarySw.ElapsedMilliseconds / 1000.0;
                    _logger.LogInformation(
                        "TxWorker 送信要約: 送信={Sent}({Rate:F0}/s) 破棄={Dropped} 滞留(通常={NQ}/連続={CQ}/高={HQ}) " +
                        "enqueue→送信[平均={AvgLat:F0}ms 最大={MaxLat:F0}ms] Write[平均={AvgW:F1}ms 最大={MaxW:F1}ms]",
                        sentInWindow, sentInWindow / sec, droppedInWindow,
                        _transport.NormalReader.Count, _transport.ContinuousReader.Count, _transport.HighPriorityReader.Count,
                        sentInWindow > 0 ? sumLatencyMs / sentInWindow : 0, maxLatencyMs,
                        sentInWindow > 0 ? sumWriteMs / sentInWindow : 0, maxWriteMs);
                    summarySw.Restart();
                    sentInWindow = droppedInWindow = 0;
                    sumLatencyMs = maxLatencyMs = sumWriteMs = maxWriteMs = 0;
                }

                SendEnvelope? envelope = null;

                // 高優先 → 連続(最新値) → 通常 の順に消化。
                // 連続チャネルはフェード等のライブ補間フレーム（最新値のみ保持）で、通常チャネルより
                // 先に消化してアニメーションを滞りなく送る。連続は容量が小さく約20ms毎にしか補充されない
                // ため、消化に余裕があれば自然に空となり通常チャネルも読まれる（通常の恒常的な飢餓は生じない）。
                if (_transport.HighPriorityReader.TryRead(out envelope))
                {
                    // high priority packet available
                }
                else if (_transport.ContinuousReader.TryRead(out envelope))
                {
                    // continuous (latest-value) frame available
                }
                else if (_transport.NormalReader.TryRead(out envelope))
                {
                    // normal packet available
                }
                else
                {
                    // 全チャネル空 → いずれかにデータが来るまで待機
                    var highTask = _transport.HighPriorityReader.WaitToReadAsync(stoppingToken).AsTask();
                    var contTask = _transport.ContinuousReader.WaitToReadAsync(stoppingToken).AsTask();
                    var normalTask = _transport.NormalReader.WaitToReadAsync(stoppingToken).AsTask();
                    await Task.WhenAny(highTask, contTask, normalTask);
                    continue; // ループ先頭に戻ってTryReadで取得
                }

                // Deadline超過チェック
                if (envelope.IsExpired)
                {
                    _logger.LogWarning("Deadline超過パケット破棄 [{OpId}]", envelope.OperationId ?? "-");
                    droppedInWindow++;
                    continue;
                }

                var writeSw = System.Diagnostics.Stopwatch.StartNew();
                await SendToTargetPorts(envelope);
                writeSw.Stop();

                // 診断カウンタ更新（enqueue→送信の遅延は SendToTargetPorts 内で記録済みの GetElapsedMs と同義）
                var latMs = envelope.GetElapsedMs();
                var writeMs = writeSw.Elapsed.TotalMilliseconds;
                sentInWindow++;
                sumLatencyMs += latMs; if (latMs > maxLatencyMs) maxLatencyMs = latMs;
                sumWriteMs += writeMs; if (writeMs > maxWriteMs) maxWriteMs = writeMs;

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
            // 各ポートへ並列に書き込む（ポートは独立した COM なので同時書き込みは安全）。
            // 逐次 foreach では 4 ポート分の書き込み時間が直列に積み上がり、1 エンベロープの処理時間を
            // 押し上げてキュー消化の律速になっていた（BUG-20260928-01）。並列化で 1 エンベロープあたりの
            // 実処理をおよそ 1 ポート分まで短縮し、消化レートを生産（約50fps）より上へ引き上げる。
            if (targetPorts.Count == 1)
            {
                WriteToPort(targetPorts[0], envelope, attempt, retransmitCount);
            }
            else if (targetPorts.Count > 1)
            {
                var tasks = new Task[targetPorts.Count];
                for (int i = 0; i < targetPorts.Count; i++)
                {
                    var sp = targetPorts[i];
                    tasks[i] = Task.Run(() => WriteToPort(sp, envelope, attempt, retransmitCount));
                }
                await Task.WhenAll(tasks);
            }

            // 最終回は待機不要
            if (attempt < retransmitCount - 1 && retransmitIntervalMs > 0)
            {
                await Task.Delay(retransmitIntervalMs);
            }
        }
    }

    /// <summary>
    /// 1 ポートへの書き込み＋計測＋エラー処理。SendToTargetPorts から各ポート並列（Task.Run）で
    /// 呼ばれるため、ここで触れる共有状態はスレッドセーフなものに限る：
    /// ILogger（スレッドセーフ）／ConcurrentQueue ベースの LatencyTracker／lock 済みの
    /// MarkPortDisconnected／参照代入のみの SetLastError（最後勝ちで無害）。
    /// </summary>
    private void WriteToPort(SerialPort sp, SendEnvelope envelope, int attempt, int retransmitCount)
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
