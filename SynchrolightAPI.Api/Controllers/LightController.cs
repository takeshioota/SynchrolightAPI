using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/light")]
public class LightController(LightingService lighting, ICommandBuilder cmd, ITransport transport, SequenceRecorder recorder) : ControllerBase
{
    // POST /api/light/global — A2
    [HttpPost("global")]
    public async Task<IActionResult> Global([FromBody] GlobalColorRequest req, CancellationToken ct)
    {
        var rgb = req.Color.ToRgb();
        recorder.RecordColor(rgb.R, rgb.G, rgb.B);
        await lighting.SetGlobalColorAsync(rgb, ct);
        return Ok(new ApiResponse(true,
            Message: $"A2 Global color set to ({rgb.R},{rgb.G},{rgb.B})"));
    }

    // POST /api/light/rows — AA (複数行同色)
    [HttpPost("rows")]
    public async Task<IActionResult> Rows([FromBody] RowsRequest req, CancellationToken ct)
    {
        var rgb = req.Color.ToRgb();
        var target = new Target.MultiRows((byte)req.Field, (ushort)req.StartRow, (ushort)req.RowLen);
        var packet = cmd.BuildSetColor(target, rgb);
        await transport.EnqueueAsync(packet, ct);
        return Ok(new ApiResponse(true,
            Message: $"AA Rows {req.StartRow}-{req.StartRow + req.RowLen - 1} set to ({rgb.R},{rgb.G},{rgb.B})"));
    }

    // POST /api/light/rows/each — A3 (行制御、自動分割あり)
    [HttpPost("rows/each")]
    public async Task<IActionResult> RowsEach([FromBody] RowsEachRequest req, CancellationToken ct)
    {
        byte field = (byte)req.Field;
        ushort startRow = (ushort)req.StartRow;

        if (req.Colors != null && req.Colors.Length > 0)
        {
            // 行別色: colorsの配列長 = len
            var colors = req.Colors;
            int len = colors.Length;
            int sent = 0;
            int frames = 0;

            while (sent < len)
            {
                int chunkLen = Math.Min(len - sent, 8);
                var chunk = colors.Skip(sent).Take(chunkLen)
                    .Select(c => (c.R, c.G, c.B)).ToArray();
                var packet = LightProtocol.BuildA3_Rows(field, (ushort)(startRow + sent), (byte)chunkLen, chunk);
                await transport.EnqueueAsync(packet, ct);
                sent += chunkLen;
                frames++;
            }

            return Ok(new ApiResponse(true,
                Message: $"A3 Rows {startRow}-{startRow + len - 1} set ({frames} frames)"));
        }
        else if (req.Color != null && req.Len.HasValue)
        {
            // 全行同色: LightingServiceの自動分割を利用
            var rgb = req.Color.ToRgb();
            await lighting.SetRowColorAsync(field, startRow, (byte)req.Len.Value, rgb, ct);
            int frames = (req.Len.Value + 7) / 8;
            return Ok(new ApiResponse(true,
                Message: $"A3 Rows {startRow}-{startRow + req.Len.Value - 1} set ({frames} frames)"));
        }

        return BadRequest(new ApiResponse(false, Error: "Either 'color'+'len' or 'colors' is required"));
    }

    // POST /api/light/cols — A8 (複数列同色)
    [HttpPost("cols")]
    public async Task<IActionResult> Cols([FromBody] ColsRequest req, CancellationToken ct)
    {
        var rgb = req.Color.ToRgb();
        var target = new Target.MultiCols((byte)req.Field, (ushort)req.StartCol, (ushort)req.ColLen);
        var packet = cmd.BuildSetColor(target, rgb);
        await transport.EnqueueAsync(packet, ct);
        return Ok(new ApiResponse(true,
            Message: $"A8 Cols {req.StartCol}-{req.StartCol + req.ColLen - 1} set to ({rgb.R},{rgb.G},{rgb.B})"));
    }

    // POST /api/light/cols/each — A4 (列制御)
    [HttpPost("cols/each")]
    public async Task<IActionResult> ColsEach([FromBody] ColsEachRequest req, CancellationToken ct)
    {
        byte field = (byte)req.Field;
        ushort startCol = (ushort)req.StartCol;

        if (req.Colors != null && req.Colors.Length > 0)
        {
            var colors = req.Colors;
            var chunk = colors.Select(c => (c.R, c.G, c.B)).ToArray();
            var packet = LightProtocol.BuildA4_Cols(field, startCol, (byte)chunk.Length, chunk);
            await transport.EnqueueAsync(packet, ct);
            return Ok(new ApiResponse(true,
                Message: $"A4 Cols {startCol}-{startCol + chunk.Length - 1} set"));
        }
        else if (req.Color != null && req.Len.HasValue)
        {
            var rgb = req.Color.ToRgb();
            var target = new Target.Cols(field, startCol, (byte)req.Len.Value);
            var packet = cmd.BuildSetColor(target, rgb);
            await transport.EnqueueAsync(packet, ct);
            return Ok(new ApiResponse(true,
                Message: $"A4 Cols {startCol}-{startCol + req.Len.Value - 1} set"));
        }

        return BadRequest(new ApiResponse(false, Error: "Either 'color'+'len' or 'colors' is required"));
    }

    // POST /api/light/points — A0
    [HttpPost("points")]
    public async Task<IActionResult> Points([FromBody] PointsRequest req, CancellationToken ct)
    {
        byte field = (byte)req.Field;
        ushort startRow = (ushort)req.StartRow;
        ushort startCol = (ushort)req.StartCol;

        if (req.Colors != null && req.Colors.Length > 0)
        {
            var colors = req.Colors.Select(c => (c.R, c.G, c.B)).ToArray();
            var packet = LightProtocol.BuildA0_Points(field, startRow, startCol, (byte)colors.Length, colors);
            await transport.EnqueueAsync(packet, ct);
            return Ok(new ApiResponse(true,
                Message: $"A0 Points ({startRow},{startCol}) len={colors.Length} set"));
        }
        else if (req.Color != null && req.Len.HasValue)
        {
            var rgb = req.Color.ToRgb();
            var target = new Target.Points(field, startRow, startCol, (byte)req.Len.Value);
            var packet = cmd.BuildSetColor(target, rgb);
            await transport.EnqueueAsync(packet, ct);
            return Ok(new ApiResponse(true,
                Message: $"A0 Points ({startRow},{startCol}) len={req.Len.Value} set"));
        }

        return BadRequest(new ApiResponse(false, Error: "Either 'color'+'len' or 'colors' is required"));
    }

    // POST /api/light/block — AC
    [HttpPost("block")]
    public async Task<IActionResult> Block([FromBody] BlockRequest req, CancellationToken ct)
    {
        var rgb = req.Color.ToRgb();
        var target = new Target.Block((byte)req.ProgNo, (byte)req.BlockNo);
        var packet = cmd.BuildSetColor(target, rgb);
        await transport.EnqueueAsync(packet, ct);
        return Ok(new ApiResponse(true,
            Message: $"AC Block prog={req.ProgNo} block={req.BlockNo} set to ({rgb.R},{rgb.G},{rgb.B})"));
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

    // POST /api/light/rx-channel — A6
    [HttpPost("rx-channel")]
    public async Task<IActionResult> RxChannel([FromBody] RxChannelRequest req, CancellationToken ct)
    {
        var packet = LightProtocol.BuildA6_SetRxChannel(
            (byte)req.Field, (ushort)req.StartRow, (byte)req.Len, (byte)req.Channel);
        await transport.EnqueueAsync(packet, ct);
        return Ok(new ApiResponse(true,
            Message: $"A6 SetRxChannel ch={req.Channel} rows {req.StartRow}-{req.StartRow + req.Len - 1}"));
    }

    // POST /api/light/block-sector — AE
    [HttpPost("block-sector")]
    public async Task<IActionResult> BlockSector([FromBody] BlockSectorRequest req, CancellationToken ct)
    {
        var rgb = req.Color.ToRgb();
        var packet = LightProtocol.BuildAE_BlockColorSector(
            (byte)req.ProgNo, (byte)req.BlockNo, rgb.R, rgb.G, rgb.B);
        await transport.EnqueueAsync(packet, ct);
        return Ok(new ApiResponse(true,
            Message: $"AE BlockSector prog={req.ProgNo} block={req.BlockNo} set to ({rgb.R},{rgb.G},{rgb.B})"));
    }

    // POST /api/light/off — A2 (黒)
    [HttpPost("off")]
    public async Task<IActionResult> Off(CancellationToken ct)
    {
        recorder.RecordOff();
        await lighting.SetGlobalColorAsync(Rgb.Black, ct);
        return Ok(new ApiResponse(true, Message: "All lights off"));
    }
}
