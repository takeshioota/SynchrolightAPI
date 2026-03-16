using System.IO.Ports;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Protocol;

namespace SynchrolightAPI.Transport;

/// <summary>
/// Channel送信キュー＋マルチポート同報トランスポート
/// </summary>
public class MultiPortTransport : ITransport, IDisposable
{
    private readonly ILogger<MultiPortTransport> _logger;
    private readonly Channel<byte[]> _channel;
    private readonly List<SerialPort> _ports = [];
    private readonly object _lock = new();
    private string? _lastError;
    private bool _disposed;

    /// <summary>キューのReaderを公開（TxWorkerServiceがDequeueに使用）</summary>
    internal ChannelReader<byte[]> Reader => _channel.Reader;

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

    public MultiPortTransport(ILogger<MultiPortTransport> logger, int queueCapacity = 256)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
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

    public Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (var name in portNames)
            {
                ct.ThrowIfCancellationRequested();

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
        }
        return Task.CompletedTask;
    }

    public async Task EnqueueAsync(byte[] packet, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(packet, ct);
    }

    public async Task EnqueueAsync(Packet32 packet, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(packet.Data, ct);
    }

    public TransportStatus GetStatus()
    {
        lock (_lock)
        {
            return new TransportStatus(
                QueueLength: _channel.Reader.Count,
                ConnectedPorts: _ports.Count(p => p.IsOpen),
                LastError: _lastError
            );
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();
        DisconnectAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
