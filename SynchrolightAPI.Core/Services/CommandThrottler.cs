using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Services;

/// <summary>
/// コマンド合成・間引きサービス。
/// 同一キーの連続コマンドを合成し、一定間隔（デフォルト16ms≒60fps）で最新のみ送信。
/// </summary>
public class CommandThrottler : IDisposable
{
    private readonly ITransport _transport;
    private readonly ILogger<CommandThrottler> _logger;
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new();
    private readonly Timer _flushTimer;
    private readonly TimeSpan _coalesceWindow;
    private bool _disposed;

    private record PendingCommand(byte[] Packet, SendOptions Options, string? OperationId, long CreatedAtTicks);

    public CommandThrottler(
        ITransport transport,
        ILogger<CommandThrottler> logger,
        TimeSpan? coalesceWindow = null)
    {
        _transport = transport;
        _logger = logger;
        _coalesceWindow = coalesceWindow ?? TimeSpan.FromMilliseconds(16);
        _flushTimer = new Timer(FlushCallback, null, _coalesceWindow, _coalesceWindow);
    }

    /// <summary>スロットリング付きエンキュー: 同キーの最新パケットのみ保持</summary>
    public Task EnqueueThrottledAsync(string key, byte[] packet, SendOptions options, string? operationId, CancellationToken ct)
    {
        var cmd = new PendingCommand(packet, options, operationId, Stopwatch.GetTimestamp());
        var prev = _pending.AddOrUpdate(key, cmd, (_, existing) =>
        {
            _logger.LogDebug("コマンド合成 key={Key} [{OpId}] → [{NewOpId}]",
                key, existing.OperationId ?? "-", operationId ?? "-");
            return cmd;
        });
        return Task.CompletedTask;
    }

    /// <summary>スロットリングをバイパスして即時エンキュー</summary>
    public async Task EnqueueImmediateAsync(byte[] packet, SendOptions options, string? operationId, CancellationToken ct)
    {
        if (_transport is MultiPortTransport mpt)
        {
            await mpt.EnqueueWithContextAsync(packet, options, operationId, ct);
        }
        else
        {
            await _transport.EnqueueAsync(packet, options, ct);
        }
    }

    private async void FlushCallback(object? state)
    {
        if (_disposed) return;

        // 全pendingコマンドを取り出して送信
        var keys = _pending.Keys.ToArray();
        foreach (var key in keys)
        {
            if (_pending.TryRemove(key, out var cmd))
            {
                try
                {
                    if (_transport is MultiPortTransport mpt)
                    {
                        await mpt.EnqueueWithContextAsync(cmd.Packet, cmd.Options, cmd.OperationId, CancellationToken.None);
                    }
                    else
                    {
                        await _transport.EnqueueAsync(cmd.Packet, cmd.Options, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Throttler flush失敗 key={Key} [{OpId}]",
                        key, cmd.OperationId ?? "-");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();

        // 残りのpendingを全てフラッシュ
        FlushCallback(null);
    }
}
