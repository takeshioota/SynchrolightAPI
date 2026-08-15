using Microsoft.Extensions.Logging;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Services;

/// <summary>
/// ライト制御オーケストレーション: Protocol→Transport橋渡し
/// OperationId付き構造化ログ・CommandThrottler経由送信対応
/// </summary>
public class LightingService
{
    private readonly ICommandBuilder _cmd;
    private readonly ITransport _transport;
    private readonly ILogger<LightingService> _logger;
    private readonly CommandThrottler? _throttler;

    public LightingService(
        ICommandBuilder cmd,
        ITransport transport,
        ILogger<LightingService> logger,
        CommandThrottler? throttler = null)
    {
        _cmd = cmd;
        _transport = transport;
        _logger = logger;
        _throttler = throttler;
    }

    /// <summary>全体一括色設定 (A2)</summary>
    public async Task SetGlobalColorAsync(Rgb color, CancellationToken ct = default)
    {
        var opId = GenerateOperationId();
        var packet = _cmd.BuildSetColor(new Target.All(), color);
        _logger.LogInformation("[{OpId}] A2 全体→({R},{G},{B}): {Hex}",
            opId, color.R, color.G, color.B, packet.ToHex());

        await EnqueueAsync("A2", packet.Data, SendOptions.Default, opId, ct);
    }

    /// <summary>全体一括色設定 (A2) + SendOptions</summary>
    public async Task SetGlobalColorAsync(Rgb color, SendOptions options, CancellationToken ct = default)
    {
        var opId = GenerateOperationId();
        var packet = _cmd.BuildSetColor(new Target.All(), color);
        _logger.LogInformation("[{OpId}] A2 全体→({R},{G},{B}): {Hex}",
            opId, color.R, color.G, color.B, packet.ToHex());

        await EnqueueAsync("A2", packet.Data, options, opId, ct);
    }

    /// <summary>水平操作 (A3): フィールドゾーン定義、水平方向に単一色で塗りつぶし。8行超は自動分割。</summary>
    public async Task SetRowColorAsync(byte field, ushort startRow, byte len, Rgb color, CancellationToken ct = default)
    {
        await SetRowColorAsync(field, startRow, len, color, SendOptions.Default, ct);
    }

    /// <summary>水平操作 (A3) + SendOptions</summary>
    public async Task SetRowColorAsync(byte field, ushort startRow, byte len, Rgb color, SendOptions options, CancellationToken ct = default)
    {
        const byte maxRowsPerPacket = 8;
        var opId = GenerateOperationId();

        if (len <= maxRowsPerPacket)
        {
            var target = new Target.Rows(field, startRow, len);
            var packet = _cmd.BuildSetColor(target, color);
            _logger.LogDebug("[{OpId}] A3 field={Field} startRow={Row} len={Len}: {Hex}",
                opId, field, startRow, len, packet.ToHex());
            await EnqueueAsync($"A3:{field:X2}:{startRow}", packet.Data, options, opId, ct);
        }
        else
        {
            var chunks = (len + maxRowsPerPacket - 1) / maxRowsPerPacket;
            _logger.LogInformation("[{OpId}] A3 自動分割: field={Field} startRow={Row} len={Len} → {Chunks}パケット",
                opId, field, startRow, len, chunks);

            int remaining = len;
            ushort currentRow = startRow;

            while (remaining > 0)
            {
                byte chunkLen = (byte)Math.Min(remaining, maxRowsPerPacket);
                var target = new Target.Rows(field, currentRow, chunkLen);
                var packet = _cmd.BuildSetColor(target, color);
                _logger.LogDebug("[{OpId}] A3 分割送信 startRow={Row} len={Len}: {Hex}",
                    opId, currentRow, chunkLen, packet.ToHex());

                // 分割パケットはスロットリングをバイパス（順序保証）
                await EnqueueDirectAsync(packet.Data, options, opId, ct);

                currentRow += chunkLen;
                remaining -= chunkLen;
            }
        }
    }

    /// <summary>送信機初期設定 (FA/FB) — 高優先度で送信</summary>
    public async Task InitializeTransmitterAsync(byte channel, byte power, CancellationToken ct = default)
    {
        var opId = GenerateOperationId();
        var highPriority = SendOptions.Default with { HighPriority = true };
        _logger.LogInformation("[{OpId}] --- 送信機初期設定 ---", opId);

        var chCmd = _cmd.BuildTxSetChannel(channel);
        _logger.LogInformation("[{OpId}] FA チャネル設定 ch={Ch}: {Hex}",
            opId, channel, LightProtocol.ToHex(chCmd));
        await EnqueueDirectAsync(chCmd, highPriority, opId, ct);

        await Task.Delay(2000, ct); // FA(チャネル設定)送信後の待機 2000ms（実機で必要。文書§4.1の150msでは送信機がFA処理中にFBを取りこぼし電力が反映されない/大阪版と同値へ復帰 2026-08-13）

        var pwrCmd = _cmd.BuildTxSetPower(power);
        _logger.LogInformation("[{OpId}] FB 電力設定 pwr={Pwr}: {Hex}",
            opId, power, LightProtocol.ToHex(pwrCmd));
        await EnqueueDirectAsync(pwrCmd, highPriority, opId, ct);

        await Task.Delay(2000, ct); // FB(電力設定)送信後の待機 2000ms（実機で必要。文書§4.2の150msでは後続コマンドがFB処理を上書きし電力が反映されない/大阪版と同値へ復帰 2026-08-13）

        _logger.LogInformation("[{OpId}] 送信機初期設定完了", opId);
    }

    // --- 内部メソッド ---

    /// <summary>スロットリング経由でエンキュー（色コマンド等）</summary>
    private async Task EnqueueAsync(string throttleKey, byte[] packet, SendOptions options, string opId, CancellationToken ct)
    {
        if (_throttler != null)
        {
            await _throttler.EnqueueThrottledAsync(throttleKey, packet, options, opId, ct);
        }
        else
        {
            await EnqueueDirectAsync(packet, options, opId, ct);
        }
    }

    /// <summary>スロットリングをバイパスして直接エンキュー（制御コマンド・分割パケット等）</summary>
    private async Task EnqueueDirectAsync(byte[] packet, SendOptions options, string opId, CancellationToken ct)
    {
        if (_transport is MultiPortTransport mpt)
        {
            await mpt.EnqueueWithContextAsync(packet, options, opId, ct);
        }
        else
        {
            await _transport.EnqueueAsync(packet, options, ct);
        }
    }

    private static string GenerateOperationId()
        => $"op-{Guid.NewGuid():N}"[..10];
}
