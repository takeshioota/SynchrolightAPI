using System.IO.Ports;
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
            s.HighPriorityQueueLength,
            s.ConnectedPorts,
            s.DisconnectedPorts,
            s.LastError,
            Ports = ports
        }));
    }

    // GET /api/transport/scan — 利用可能なCOMポート一覧
    [HttpGet("scan")]
    public IActionResult Scan()
    {
        var ports = SerialPort.GetPortNames().OrderBy(n => n).ToArray();
        return Ok(new ApiResponse(true, Data: new { ports }));
    }

    // POST /api/transport/keepalive — 指定パケットを再送
    [HttpPost("keepalive")]
    public async Task<IActionResult> KeepAlive([FromBody] KeepAliveRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Base64Packet))
            return BadRequest(new ApiResponse(false, Error: "base64Packet is required"));

        var packet = Convert.FromBase64String(req.Base64Packet);
        await transport.EnqueueAsync(packet, ct);
        return Ok(new ApiResponse(true, Message: "Keep-alive sent"));
    }
}
