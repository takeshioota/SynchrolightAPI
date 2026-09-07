using System.Diagnostics;
using System.IO.Ports;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Protocol;

namespace SynchrolightAPI.Transport;

/// <summary>
/// Channel送信キュー（2チャネル: 高優先/通常）＋マルチポート同報/ルーティングトランスポート
/// </summary>
public class MultiPortTransport : ITransport, IDisposable
{
    private readonly ILogger<MultiPortTransport> _logger;
    private readonly Channel<SendEnvelope> _highPriorityChannel;
    private readonly Channel<SendEnvelope> _normalChannel;
    private readonly List<SerialPort> _ports = [];
    private readonly HashSet<string> _expectedPortNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ZoneRouter? _zoneRouter;
    private string? _lastError;
    private bool _disposed;

    /// <summary>高優先キューのReader（TxWorkerServiceが使用）</summary>
    internal ChannelReader<SendEnvelope> HighPriorityReader => _highPriorityChannel.Reader;

    /// <summary>通常キューのReader（TxWorkerServiceが使用）</summary>
    internal ChannelReader<SendEnvelope> NormalReader => _normalChannel.Reader;

    /// <summary>接続中のSerialPort一覧（TxWorkerServiceが送信に使用）</summary>
    internal IReadOnlyList<SerialPort> ConnectedPorts
    {
        get
        {
            lock (_lock)
            {
                return _ports.Where(p => p.IsOpen).ToList().AsReadOnly();
            }
        }
    }

    /// <summary>最終エラーを記録（TxWorkerServiceから呼び出し）</summary>
    internal void SetLastError(string error)
    {
        _lastError = error;
    }

    /// <summary>ポートを切断済みとしてマーク（TxWorkerServiceから呼び出し）</summary>
    internal void MarkPortDisconnected(string portName)
    {
        lock (_lock)
        {
            var sp = _ports.FirstOrDefault(p =>
                string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase));

            if (sp != null)
            {
                try
                {
                    if (sp.IsOpen) sp.Close();
                }
                catch { /* ignore close errors */ }

                _logger.LogWarning("ポート {PortName} を切断済みとしてマーク", portName);
            }
        }
    }

    // BUG-20260904-02(No.14): 高負荷/VM 環境で SerialPort.GetPortNames()（レジストリ列挙）が
    // 一時的に空／不完全を返すことがあり、開いているポートが一斉に「消失」と誤検知されて
    // 全ポート切断→再接続が走り、信号が数秒途切れる（端末はセルフモードに落ちて消灯）。
    // 単発のグリッチで切断扱いしないよう、(1) 列挙が空ならこのパスをスキップし、
    // (2) 各ポートは連続 MissingThreshold 回消えて初めて切断扱いにする（デバウンス）。
    // ※実際にケーブルが抜けている場合、送信中は Write 例外で即検出されるため本デバウンスの影響は
    //   「待機中の抜線検出が最大 MissingThreshold×チェック間隔ぶん遅れる」だけで実害は無い。
    private const int MissingThreshold = 3; // PortHealthMonitor の 5 秒間隔 × 3 ≒ 15 秒 連続消失で確定
    private readonly Dictionary<string, int> _missingStreak = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 物理的に存在しなくなった（USB 抜去等で OS のポート一覧から消えた）オープン中ポートを
    /// 検出し、切断済みとしてクローズする。SerialPort.IsOpen は USB 抜去後も true のまま残る
    /// ため、OS のポート一覧（SerialPort.GetPortNames）と突き合わせて死活を判定する。
    /// 待機中（送信が無く Write 例外による検出が働かない状態）でも切断を検知するためのハートビート。
    /// PortHealthMonitor から定期的に呼び出される。
    /// 高負荷時の列挙グリッチによる誤検知を避けるため、空応答スキップ＋連続消失デバウンスを行う。
    /// </summary>
    internal void ReconcilePhysicalPorts()
    {
        string[] available;
        try { available = SerialPort.GetPortNames(); }
        catch { return; }

        List<SerialPort> openPorts;
        lock (_lock)
        {
            openPorts = _ports.Where(p => p.IsOpen).ToList();
        }
        if (openPorts.Count == 0) return;

        // グリッチ保護: 開いているポートがあるのに列挙が空を返した場合は、ケーブル一斉抜けではなく
        // 高負荷によるレジストリ列挙の一時的失敗とみなし、このパスでは何もしない（切断扱いしない）。
        if (available.Length == 0)
        {
            _logger.LogDebug("GetPortNames が空を返却（高負荷による一時的な列挙失敗とみなしスキップ）。");
            return;
        }

        var availableSet = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);

        foreach (var sp in openPorts)
        {
            if (availableSet.Contains(sp.PortName))
            {
                // 見えている → ストリークをリセット（過去の一時的不在を打ち消す）
                _missingStreak.Remove(sp.PortName);
                continue;
            }

            _missingStreak.TryGetValue(sp.PortName, out var streak);
            streak++;
            _missingStreak[sp.PortName] = streak;

            if (streak < MissingThreshold)
            {
                // まだ確定しない（単発／短時間のグリッチ耐性）。次パスで復帰すればリセットされる。
                _logger.LogDebug(
                    "ポート {PortName} が OS 一覧に一時的に不在（{Streak}/{Threshold}）。デバウンス中で切断扱いにしません。",
                    sp.PortName, streak, MissingThreshold);
                continue;
            }

            _logger.LogWarning(
                "ポート {PortName} が OS のポート一覧から {Threshold} 回連続で消失（ケーブル抜け等）。切断としてマークします。",
                sp.PortName, MissingThreshold);
            SetLastError($"{sp.PortName}: disconnected (cable removed)");
            MarkPortDisconnected(sp.PortName);
            _missingStreak.Remove(sp.PortName);
        }
    }

    /// <summary>切断されたポートの再接続を試行</summary>
    internal bool TryReconnectPort(string portName)
    {
        lock (_lock)
        {
            var existing = _ports.FirstOrDefault(p =>
                string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (existing.IsOpen) return true; // already connected

                try
                {
                    existing.Dispose();
                    _ports.Remove(existing);
                }
                catch { /* ignore */ }
            }

            try
            {
                var sp = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };
                sp.Open();
                _ports.Add(sp);
                _logger.LogInformation("COMポート {PortName} を再接続しました", portName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "COMポート {PortName} の再接続に失敗", portName);
                _lastError = $"{portName}: reconnect failed - {ex.Message}";
                return false;
            }
        }
    }

    public MultiPortTransport(
        ILogger<MultiPortTransport> logger,
        int queueCapacity = 256,
        ZoneRouter? zoneRouter = null)
    {
        _logger = logger;
        _zoneRouter = zoneRouter;

        _highPriorityChannel = Channel.CreateBounded<SendEnvelope>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _normalChannel = Channel.CreateBounded<SendEnvelope>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public IReadOnlyList<PortInfo> ListPorts()
    {
        lock (_lock)
        {
            return _ports.Select(p => new PortInfo(p.PortName, p.IsOpen)).ToList().AsReadOnly();
        }
    }

    /// <summary>接続を期待するポート名一覧</summary>
    internal IReadOnlySet<string> ExpectedPortNames
    {
        get
        {
            lock (_lock) { return _expectedPortNames.ToHashSet(StringComparer.OrdinalIgnoreCase); }
        }
    }

    /// <summary>切断中のポート数</summary>
    internal int DisconnectedPortCount
    {
        get
        {
            lock (_lock)
            {
                return _expectedPortNames.Count - _ports.Count(p => p.IsOpen);
            }
        }
    }

    public Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (var name in portNames)
            {
                ct.ThrowIfCancellationRequested();
                _expectedPortNames.Add(name);

                try
                {
                    var sp = new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
                    {
                        Handshake = Handshake.None,
                        ReadTimeout = 500,
                        WriteTimeout = 500
                    };
                    sp.Open();
                    _ports.Add(sp);
                    _logger.LogInformation("COMポート {PortName} を開きました", name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "COMポート {PortName} のオープンに失敗しました", name);
                    _lastError = $"{name}: {ex.Message}";
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        lock (_lock)
        {
            foreach (var sp in _ports)
            {
                try
                {
                    if (sp.IsOpen)
                    {
                        sp.Close();
                        _logger.LogInformation("COMポート {PortName} を閉じました", sp.PortName);
                    }
                    sp.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "COMポート {PortName} のクローズ中にエラー", sp.PortName);
                }
            }
            _ports.Clear();
            _expectedPortNames.Clear();
        }
        return Task.CompletedTask;
    }

    // --- EnqueueAsync: 後方互換（SendOptionsなし） ---

    public Task EnqueueAsync(byte[] packet, CancellationToken ct = default)
        => EnqueueAsync(packet, SendOptions.Default, ct);

    public Task EnqueueAsync(Packet32 packet, CancellationToken ct = default)
        => EnqueueAsync(packet.Data, SendOptions.Default, ct);

    // --- EnqueueAsync: SendOptions付き ---

    public async Task EnqueueAsync(byte[] packet, SendOptions options, CancellationToken ct = default)
    {
        var targetPorts = _zoneRouter?.ResolveTargetPorts(packet, options);

        var envelope = new SendEnvelope(
            Packet: packet,
            Options: options,
            TargetPortNames: targetPorts,
            EnqueuedAtTicks: Stopwatch.GetTimestamp(),
            OperationId: null
        );

        var channel = options.HighPriority ? _highPriorityChannel : _normalChannel;
        await channel.Writer.WriteAsync(envelope, ct);
    }

    public Task EnqueueAsync(Packet32 packet, SendOptions options, CancellationToken ct = default)
        => EnqueueAsync(packet.Data, options, ct);

    /// <summary>OperationId付きでエンキュー（LightingService等から使用）</summary>
    internal async Task EnqueueWithContextAsync(
        byte[] packet, SendOptions options, string? operationId, CancellationToken ct = default)
    {
        var targetPorts = _zoneRouter?.ResolveTargetPorts(packet, options);

        var envelope = new SendEnvelope(
            Packet: packet,
            Options: options,
            TargetPortNames: targetPorts,
            EnqueuedAtTicks: Stopwatch.GetTimestamp(),
            OperationId: operationId
        );

        var channel = options.HighPriority ? _highPriorityChannel : _normalChannel;
        await channel.Writer.WriteAsync(envelope, ct);

        _logger.LogDebug("Enqueue [{OpId}] priority={Priority} queue={Queue}",
            operationId ?? "-", options.HighPriority ? "HIGH" : "NORMAL",
            channel.Reader.Count);
    }

    public void FlushQueue()
    {
        // 通常・高優先の両チャネルを破棄する。
        // 高優先チャネルも消化対象に含めないと、TxWorker が高優先を先に読むため、
        // 残存した高優先パケットが Emergency 等の新コマンド送信より前に流れてしまう。
        while (_normalChannel.Reader.TryRead(out _)) { }
        while (_highPriorityChannel.Reader.TryRead(out _)) { }
        _logger.LogInformation("送信キュー（通常・高優先）をフラッシュしました");
    }

    public TransportStatus GetStatus()
    {
        lock (_lock)
        {
            return new TransportStatus(
                QueueLength: _normalChannel.Reader.Count,
                HighPriorityQueueLength: _highPriorityChannel.Reader.Count,
                ConnectedPorts: _ports.Count(p => p.IsOpen),
                DisconnectedPorts: Math.Max(0, _expectedPortNames.Count - _ports.Count(p => p.IsOpen)),
                LastError: _lastError
            );
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _highPriorityChannel.Writer.TryComplete();
        _normalChannel.Writer.TryComplete();
        DisconnectAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
