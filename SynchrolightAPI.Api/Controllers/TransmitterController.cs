using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/transmitter")]
public class TransmitterController(LightingService lighting, ITransport transport) : ControllerBase
{
    [HttpPost("init")]
    public async Task<IActionResult> Init([FromBody] TransmitterInitRequest req, CancellationToken ct)
    {
        if (req.Channel < 1 || req.Channel > 4)
            return BadRequest(new ApiResponse(false, Error: "channel must be 1-4"));
        if (req.Power < 0 || req.Power > 3)
            return BadRequest(new ApiResponse(false, Error: "power must be 0-3"));

        await lighting.InitializeTransmitterAsync((byte)req.Channel, (byte)req.Power, ct);

        return Ok(new ApiResponse(true,
            Message: $"Transmitter initialized: ch={req.Channel}, pwr={req.Power}"));
    }

    // POST /api/transmitter/set-channel — FA
    [HttpPost("set-channel")]
    public async Task<IActionResult> SetChannel([FromBody] SetChannelRequest req, CancellationToken ct)
    {
        var packet = LightProtocol.BuildTxSetChannel((byte)req.Channel);
        await transport.EnqueueAsync(packet, SendOptions.Default with { HighPriority = true }, ct);
        return Ok(new ApiResponse(true, Message: $"Tx channel set to {req.Channel}"));
    }

    // POST /api/transmitter/set-power — FB
    [HttpPost("set-power")]
    public async Task<IActionResult> SetPower([FromBody] SetPowerRequest req, CancellationToken ct)
    {
        var packet = LightProtocol.BuildTxSetPower((byte)req.Power);
        await transport.EnqueueAsync(packet, SendOptions.Default with { HighPriority = true }, ct);
        return Ok(new ApiResponse(true, Message: $"Tx power set to {req.Power}"));
    }
}
