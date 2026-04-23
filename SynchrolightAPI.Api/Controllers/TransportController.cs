using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/transport")]
public class TransportController(ITransport transport) : ControllerBase
{
    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest req)
    {
        if (req.PortNames == null || req.PortNames.Length == 0)
            return BadRequest(new ApiResponse(false, Error: "portNames is required"));

        await transport.ConnectAsync(req.PortNames);
        var ports = transport.ListPorts();
        return Ok(new ApiResponse(true,
            Message: $"{ports.Count(p => p.IsConnected)} port(s) connected",
            Data: new { connectedPorts = ports }));
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await transport.DisconnectAsync();
        return Ok(new ApiResponse(true, Message: "Disconnected"));
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var s = transport.GetStatus();
        var ports = transport.ListPorts();
        return Ok(new ApiResponse(true, Data: new
        {
            s.QueueLength,
            s.ConnectedPorts,
            s.LastError,
            Ports = ports
        }));
    }
}
