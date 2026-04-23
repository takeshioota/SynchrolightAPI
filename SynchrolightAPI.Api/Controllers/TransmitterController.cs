using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/transmitter")]
public class TransmitterController(LightingService lighting) : ControllerBase
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
}
