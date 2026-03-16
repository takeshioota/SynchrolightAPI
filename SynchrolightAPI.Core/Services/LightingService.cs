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

    /// <summary>行制御 (A3): A3は1フレーム最大8行のため自動分割</summary>
    public async Task SetRowColorAsync(byte field, ushort startRow, byte len, Rgb color, CancellationToken ct = default)
    {
        const int maxA3Len = 8;
        int remaining = len;
        ushort currentRow = startRow;

        while (remaining > 0)
        {
            byte batchLen = (byte)Math.Min(remaining, maxA3Len);
            var target = new Target.Rows(field, currentRow, batchLen);
            var packet = _cmd.BuildSetColor(target, color);
            _logger.LogDebug("A3 row={Row} len={Len}: {Hex}", currentRow, batchLen, packet.ToHex());
            await _transport.EnqueueAsync(packet, ct);

            currentRow += batchLen;
            remaining -= batchLen;
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
