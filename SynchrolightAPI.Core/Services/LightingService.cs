using Microsoft.Extensions.Logging;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Services;

/// <summary>
/// ライト制御オーケストレーション: Protocol→Transport橋渡し
/// </summary>
public class LightingService
{
    private readonly ICommandBuilder _cmd;
    private readonly ITransport _transport;
    private readonly ILogger<LightingService> _logger;

    public LightingService(
        ICommandBuilder cmd,
        ITransport transport,
        ILogger<LightingService> logger)
    {
        _cmd = cmd;
        _transport = transport;
        _logger = logger;
    }

    /// <summary>全体一括色設定 (A2)</summary>
    public async Task SetGlobalColorAsync(Rgb color, CancellationToken ct = default)
    {
        var packet = _cmd.BuildSetColor(new Target.All(), color);
        _logger.LogInformation("A2 全体→({R},{G},{B}): {Hex}", color.R, color.G, color.B, packet.ToHex());
        await _transport.EnqueueAsync(packet, ct);
    }

    /// <summary>水平操作 (A3): フィールドゾーン定義、水平方向に単一色で塗りつぶし。8行超は自動分割。</summary>
    public async Task SetRowColorAsync(byte field, ushort startRow, byte len, Rgb color, CancellationToken ct = default)
    {
        const byte maxRowsPerPacket = 8;

        if (len <= maxRowsPerPacket)
        {
            var target = new Target.Rows(field, startRow, len);
            var packet = _cmd.BuildSetColor(target, color);
            _logger.LogDebug("A3 startRow={Row} len={Len}: {Hex}", startRow, len, packet.ToHex());
            await _transport.EnqueueAsync(packet, ct);
        }
        else
        {
            _logger.LogInformation("A3 自動分割: startRow={Row} len={Len} → {Chunks}パケット",
                startRow, len, (len + maxRowsPerPacket - 1) / maxRowsPerPacket);

            int remaining = len;
            ushort currentRow = startRow;

            while (remaining > 0)
            {
                byte chunkLen = (byte)Math.Min(remaining, maxRowsPerPacket);
                var target = new Target.Rows(field, currentRow, chunkLen);
                var packet = _cmd.BuildSetColor(target, color);
                _logger.LogDebug("A3 分割送信 startRow={Row} len={Len}: {Hex}", currentRow, chunkLen, packet.ToHex());
                await _transport.EnqueueAsync(packet, ct);

                currentRow += chunkLen;
                remaining -= chunkLen;
            }
        }
    }

    /// <summary>送信機初期設定 (FA/FB)</summary>
    public async Task InitializeTransmitterAsync(byte channel, byte power, CancellationToken ct = default)
    {
        _logger.LogInformation("--- 送信機初期設定 ---");

        var chCmd = _cmd.BuildTxSetChannel(channel);
        _logger.LogInformation("FA チャネル設定 ch={Ch}: {Hex}", channel, LightProtocol.ToHex(chCmd));
        await _transport.EnqueueAsync(chCmd, ct);

        // チャネル設定後の待機
        await Task.Delay(2000, ct);

        var pwrCmd = _cmd.BuildTxSetPower(power);
        _logger.LogInformation("FB 電力設定 pwr={Pwr}: {Hex}", power, LightProtocol.ToHex(pwrCmd));
        await _transport.EnqueueAsync(pwrCmd, ct);

        // 電力設定後の待機
        await Task.Delay(2000, ct);

        _logger.LogInformation("送信機初期設定完了");
    }
}
