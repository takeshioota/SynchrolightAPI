using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Api.Services;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Models;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/light")]
public class LightController(ICommandBuilder cmd, ITransport transport, SequenceRecorder recorder, EffectRunnerService runner) : ControllerBase
{
    // POST /api/light/global — A2
    [HttpPost("global")]
    public IActionResult Global([FromBody] GlobalColorRequest req)
    {
        var rgb = req.Color.ToRgb();
        byte field = (byte)(req.Field ?? 0x00);
        recorder.RecordColor(rgb.R, rgb.G, rgb.B);
        runner.StartColorHold(field, rgb);
        return Ok(new ApiResponse(true,
            Message: $"A2 Global color set to ({rgb.R},{rgb.G},{rgb.B})"));
    }

    // POST /api/light/rows — AA (複数行同色)
    [HttpPost("rows")]
    public IActionResult Rows([FromBody] RowsRequest req)
    {
        var rgb = req.Color.ToRgb();
        var target = new Target.MultiRows((byte)req.Field, (ushort)req.StartRow, (ushort)req.RowLen);
        var packet = cmd.BuildSetColor(target, rgb);
        runner.StartPacketHold(packet.Data);
        return Ok(new ApiResponse(true,
            Message: $"AA Rows {req.StartRow}-{req.StartRow + req.RowLen - 1} set to ({rgb.R},{rgb.G},{rgb.B})"));
    }

    // POST /api/light/rows/each — A3 (行制御、自動分割あり)
    [HttpPost("rows/each")]
    public IActionResult RowsEach([FromBody] RowsEachRequest req)
    {
        byte field = (byte)req.Field;
        ushort startRow = (ushort)req.StartRow;

        if (req.Colors != null && req.Colors.Length > 0)
        {
            // 行別色: 8行ごとに A3 パケットへ分割し、全パケットをバースト保持（>8行対応）。
            // 同一 A3 パケット内の行は1パケット＝同時反映。パケット間のみ僅かなずれ（連結バースト）。
            var all = req.Colors.Select(c => (c.R, c.G, c.B)).ToArray();
            var a3packets = new List<byte[]>();
            for (int off = 0; off < all.Length; off += 8)
            {
                var chunk = all.Skip(off).Take(8).ToArray();
                a3packets.Add(LightProtocol.BuildA3_Rows(field, (ushort)(startRow + off), (byte)chunk.Length, chunk));
            }
            // retransmit 指定 or 複数パケット時はバースト保持（back-to-back 反復送出でRF取りこぼしを抑える）。
            if (req.Retransmit.HasValue || a3packets.Count > 1)
                runner.StartPacketBurstHold(a3packets, req.BurstGapMs ?? 80, req.Retransmit ?? 1);
            else
                runner.StartPacketHold(a3packets[0]);

            return Ok(new ApiResponse(true,
                Message: $"A3 Rows {startRow}-{startRow + all.Length - 1} set ({a3packets.Count}pkt, retransmit={req.Retransmit ?? 1})"));
        }
        else if (req.Color != null && req.Len.HasValue)
        {
            // 全行同色: 単一A3パケットでホールド送信
            var rgb = req.Color.ToRgb();
            byte len = (byte)Math.Min(req.Len.Value, 8);
            var packet = LightProtocol.BuildA3_Rows(field, startRow, len, rgb.R, rgb.G, rgb.B);
            runner.StartPacketHold(packet);
            return Ok(new ApiResponse(true,
                Message: $"A3 Rows {startRow}-{startRow + req.Len.Value - 1} set"));
        }

        return BadRequest(new ApiResponse(false, Error: "Either 'color'+'len' or 'colors' is required"));
    }

    // POST /api/light/cols — A8 (複数列同色)
    [HttpPost("cols")]
    public IActionResult Cols([FromBody] ColsRequest req)
    {
        var rgb = req.Color.ToRgb();
        var target = new Target.MultiCols((byte)req.Field, (ushort)req.StartCol, (ushort)req.ColLen);
        var packet = cmd.BuildSetColor(target, rgb);
        runner.StartPacketHold(packet.Data);
        return Ok(new ApiResponse(true,
            Message: $"A8 Cols {req.StartCol}-{req.StartCol + req.ColLen - 1} set to ({rgb.R},{rgb.G},{rgb.B})"));
    }

    // POST /api/light/cols/each — A4 (列制御)
    [HttpPost("cols/each")]
    public IActionResult ColsEach([FromBody] ColsEachRequest req)
    {
        byte field = (byte)req.Field;
        ushort startCol = (ushort)req.StartCol;

        if (req.Colors != null && req.Colors.Length > 0)
        {
            var colors = req.Colors;
            var chunk = colors.Select(c => (c.R, c.G, c.B)).ToArray();
            var packet = LightProtocol.BuildA4_Cols(field, startCol, (byte)chunk.Length, chunk);
            runner.StartPacketHold(packet);
            return Ok(new ApiResponse(true,
                Message: $"A4 Cols {startCol}-{startCol + chunk.Length - 1} set"));
        }
        else if (req.Color != null && req.Len.HasValue)
        {
            var rgb = req.Color.ToRgb();
            var target = new Target.Cols(field, startCol, (byte)req.Len.Value);
            var packet = cmd.BuildSetColor(target, rgb);
            runner.StartPacketHold(packet.Data);
            return Ok(new ApiResponse(true,
                Message: $"A4 Cols {startCol}-{startCol + req.Len.Value - 1} set"));
        }

        return BadRequest(new ApiResponse(false, Error: "Either 'color'+'len' or 'colors' is required"));
    }

    // POST /api/light/points — A0
    [HttpPost("points")]
    public IActionResult Points([FromBody] PointsRequest req)
    {
        byte field = (byte)req.Field;
        ushort startRow = (ushort)req.StartRow;
        ushort startCol = (ushort)req.StartCol;

        if (req.Colors != null && req.Colors.Length > 0)
        {
            var colors = req.Colors.Select(c => (c.R, c.G, c.B)).ToArray();
            var packet = LightProtocol.BuildA0_Points(field, startRow, startCol, (byte)colors.Length, colors);
            runner.StartPacketHold(packet);
            return Ok(new ApiResponse(true,
                Message: $"A0 Points ({startRow},{startCol}) len={colors.Length} set"));
        }
        else if (req.Color != null && req.Len.HasValue)
        {
            var rgb = req.Color.ToRgb();
            var target = new Target.Points(field, startRow, startCol, (byte)req.Len.Value);
            var packet = cmd.BuildSetColor(target, rgb);
            runner.StartPacketHold(packet.Data);
            return Ok(new ApiResponse(true,
                Message: $"A0 Points ({startRow},{startCol}) len={req.Len.Value} set"));
        }

        return BadRequest(new ApiResponse(false, Error: "Either 'color'+'len' or 'colors' is required"));
    }

    // POST /api/light/block — AC
    [HttpPost("block")]
    public IActionResult Block([FromBody] BlockRequest req)
    {
        var rgb = req.Color.ToRgb();
        var target = new Target.Block((byte)req.ProgNo, (byte)req.BlockNo);
        var packet = cmd.BuildSetColor(target, rgb);
        runner.StartPacketHold(packet.Data);
        return Ok(new ApiResponse(true,
            Message: $"AC Block prog={req.ProgNo} block={req.BlockNo} set to ({rgb.R},{rgb.G},{rgb.B})"));
    }

    // POST /api/light/blocks — AC を複数ブロック分「バースト保持」（レインボー7色などの同時点灯・ずれ低減用）
    [HttpPost("blocks")]
    public IActionResult Blocks([FromBody] BlocksBurstRequest req)
    {
        if (req.Entries is null || req.Entries.Length == 0)
            return BadRequest(new ApiResponse(false, Error: "entries は1件以上必要です"));

        var packets = new List<byte[]>(req.Entries.Length);
        foreach (var e in req.Entries)
        {
            var rgb = e.Color.ToRgb();
            packets.Add(LightProtocol.BuildAC_BlockColor((byte)e.ProgNo, (byte)e.BlockNo, rgb.R, rgb.G, rgb.B));
        }

        int gap = req.BurstGapMs ?? 100;
        int rt = req.Retransmit ?? 1;
        int interGap = Math.Max(0, req.InterPacketGapMs ?? 0);

        // InterPacketGapMs 指定時: 送出レート検証モード。連結せず1パケットずつ interGap(ms) 間隔で送出し、
        // 送信機に届くACパケットの発行間隔を可変にして送信機の追従挙動を観察する。間隔を支配変数にするため
        // concat は無効化し（連結すると発行が1回になり間隔が意味を持たない）、再送も要求値どおり適用する。
        if (interGap > 0)
        {
            runner.StartPacketBurstHold(packets, gap, rt, interGap);
            return Ok(new ApiResponse(true,
                Message: $"AC rate-test {packets.Count} blocks (interGap={interGap}ms, gap={gap}ms, retransmit={rt})"));
        }

        // Concat(既定): 全パケットを連結して1書込みで送出＝パケット間の送出間隔(5ms×)を無くし、
        // ブロック間の点灯ずれをシリアル送出時間(≈19ms/7×32byte@115200)まで最小化する。
        // retransmit>1 で連結ブロブを back-to-back 反復送出＝RF取りこぼし対策（ずれは増えない）。
        if (req.Concat ?? true)
        {
            var blob = new byte[packets.Sum(p => p.Length)];
            int off = 0;
            foreach (var p in packets) { Buffer.BlockCopy(p, 0, blob, off, p.Length); off += p.Length; }
            runner.StartPacketBurstHold(new[] { blob }, gap, rt);
            return Ok(new ApiResponse(true,
                Message: $"AC concat-hold {packets.Count} blocks ({blob.Length}B / 1 write, retransmit={rt})"));
        }

        runner.StartPacketBurstHold(packets, gap, rt);
        return Ok(new ApiResponse(true,
            Message: $"AC burst-hold {packets.Count} blocks (gap={gap}ms, retransmit={rt})"));
    }

    // POST /api/light/sequence — A1
    [HttpPost("sequence")]
    public async Task<IActionResult> Sequence([FromBody] SequenceRequest req, CancellationToken ct)
    {
        var packet = LightProtocol.BuildA1_PlaySequence(req.FrameNo);
        await transport.EnqueueAsync(packet, ct);
        return Ok(new ApiResponse(true,
            Message: $"A1 Sequence play frame={req.FrameNo}"));
    }

    // POST /api/light/rx-channel — A6（前後分区・行 / 周波数変更 3.6・全体ブロードキャスト）
    [HttpPost("rx-channel")]
    public async Task<IActionResult> RxChannel([FromBody] RxChannelRequest req, CancellationToken ct)
    {
        var packet = LightProtocol.BuildA6_SetRxChannel((byte)req.Channel);
        await transport.EnqueueAsync(packet, ct);
        return Ok(new ApiResponse(true,
            Message: $"A6 SetRxChannel ch={req.Channel} (場次=00 開始行=FFFF 長さ=01)"));
    }

    // POST /api/light/rx-channel-col — AD（左右分区・列 / 周波数変更 3.7・全体ブロードキャスト）
    [HttpPost("rx-channel-col")]
    public async Task<IActionResult> RxChannelCol([FromBody] RxChannelColRequest req, CancellationToken ct)
    {
        var packet = LightProtocol.BuildAD_SetRxChannel((byte)req.Channel);
        await transport.EnqueueAsync(packet, ct);
        return Ok(new ApiResponse(true,
            Message: $"AD SetRxChannel ch={req.Channel} (場次=00 開始列=FFFF 長さ=01)"));
    }

    // POST /api/light/block-sector — AE
    [HttpPost("block-sector")]
    public IActionResult BlockSector([FromBody] BlockSectorRequest req)
    {
        var rgb = req.Color.ToRgb();
        var packet = LightProtocol.BuildAE_BlockColorSector(
            (byte)req.ProgNo, (byte)req.BlockNo, rgb.R, rgb.G, rgb.B);
        runner.StartPacketHold(packet);
        return Ok(new ApiResponse(true,
            Message: $"AE BlockSector prog={req.ProgNo} block={req.BlockNo} set to ({rgb.R},{rgb.G},{rgb.B})"));
    }

    // POST /api/light/internal-program — A1 (SNO内蔵プログラム再生)
    [HttpPost("internal-program")]
    public IActionResult InternalProgram([FromBody] InternalProgramRequest req)
    {
        runner.StartInternalProgram(req.FrameNo);
        var prog = InternalProgramTable.FindByFrameNo(req.FrameNo);
        var name = prog?.Name ?? "unknown";
        return Ok(new ApiResponse(true,
            Message: $"A1 Internal program started: frame={req.FrameNo} ({name})"));
    }

    // POST /api/light/internal-program/stop — 内蔵プログラム停止（A2黒で上書き）
    [HttpPost("internal-program/stop")]
    public IActionResult InternalProgramStop()
    {
        runner.StartColorHold(0x00, Rgb.Black);
        return Ok(new ApiResponse(true, Message: "Internal program stopped (A2 off)"));
    }

    // GET /api/light/internal-program/list — 内蔵プログラム一覧
    [HttpGet("internal-program/list")]
    public IActionResult InternalProgramList()
    {
        var programs = InternalProgramTable.Programs.Select(p => new
        {
            p.FrameNo,
            p.Name,
            p.Description
        });
        return Ok(new ApiResponse(true, Data: new { programs }));
    }

    // --- File Write (2.4GHz: 3.13-3.14) ---

    // POST /api/light/file-write/24g — 2.4GHz経由ファイル書き込み
    [HttpPost("file-write/24g")]
    public async Task<IActionResult> FileWrite24G([FromBody] FileWrite24GRequest req, CancellationToken ct)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(req.Data);
        }
        catch (FormatException)
        {
            return BadRequest(new ApiResponse(false, Error: "Invalid base64 data"));
        }

        if (data.Length == 0 || data.Length % 3 != 0)
            return BadRequest(new ApiResponse(false, Error: "Data length must be a multiple of 3 (RGB)"));

        await runner.WriteFileVia24GAsync(data, req.FrameNo, ct);
        return Ok(new ApiResponse(true,
            Message: $"File write complete: frame={req.FrameNo}, size={data.Length}bytes"));
    }

    // --- Rainbow (V4.5: 3.15-3.22) ---

    // POST /api/light/rainbow/color-table — 色テーブルのみ送信（3.15）
    [HttpPost("rainbow/color-table")]
    public async Task<IActionResult> RainbowColorTable(
        [FromBody] SendColorTableRequest req, CancellationToken ct)
    {
        if (req.Colors == null || req.Colors.Length < 2 || req.Colors.Length > 7)
            return BadRequest(new ApiResponse(false, Error: "colors must have 2-7 items"));

        var colors = req.Colors.Select(c => (c.R, c.G, c.B)).ToArray();
        await runner.SendColorTableAsync(colors, ct);
        return Ok(new ApiResponse(true,
            Message: $"Color table sent: colors={req.Colors.Length}"));
    }

    // POST /api/light/rainbow/start — レインボーエフェクト開始
    [HttpPost("rainbow/start")]
    public IActionResult RainbowStart([FromBody] StartRainbowRequest req)
    {
        if (req.Colors == null || req.Colors.Length < 2 || req.Colors.Length > 7)
            return BadRequest(new ApiResponse(false, Error: "colors must have 2-7 items"));

        var colors = req.Colors.Select(c => (c.R, c.G, c.B)).ToArray();
        runner.StartRainbow(
            req.Mode, colors, req.CycleDurationMs,
            req.BlinkPeriodMs, req.DutyRatio,
            req.FadeInMs, req.FadeOutMs);
        return Ok(new ApiResponse(true,
            Message: $"Rainbow started: mode={req.Mode}, colors={req.Colors.Length}, cycle={req.CycleDurationMs}ms"));
    }

    // POST /api/light/rainbow/stop — レインボーエフェクト停止
    [HttpPost("rainbow/stop")]
    public IActionResult RainbowStop()
    {
        runner.StopEffect();
        return Ok(new ApiResponse(true, Message: "Rainbow stopped"));
    }

    // POST /api/light/rainbow/pause — 7色ランダム一時停止（前回の色を保持）
    [HttpPost("rainbow/pause")]
    public IActionResult RainbowPause()
    {
        runner.PauseRainbow();
        return Ok(new ApiResponse(true, Message: "Rainbow paused (color held)"));
    }

    // POST /api/light/off — A2 (黒)
    [HttpPost("off")]
    public IActionResult Off()
    {
        recorder.RecordOff();
        runner.StartColorHold(0x00, Rgb.Black);
        return Ok(new ApiResponse(true, Message: "All lights off"));
    }
}
