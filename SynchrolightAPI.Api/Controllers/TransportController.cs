using System.IO.Ports;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Api.Services;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/transport")]
public class TransportController(ITransport transport, SendLogStore sendLogStore) : ControllerBase
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
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

    // GET /api/transport/log — 送信ログ取得（ポーリング用）
    [HttpGet("log")]
    public IActionResult GetLog([FromQuery] long afterSeq = 0, [FromQuery] int count = 100)
    {
        var entries = afterSeq > 0
            ? sendLogStore.GetSince(afterSeq)
            : sendLogStore.GetRecent(count);
        return Ok(new ApiResponse(true, Data: new { entries }));
    }

    // GET /api/transport/log/stream — 送信ログSSEストリーム（リアルタイム用）
    [HttpGet("log/stream")]
    public async Task GetLogStream(CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var reader = sendLogStore.Subscribe();
        try
        {
            await foreach (var entry in reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(entry, SseJsonOptions);
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            sendLogStore.Unsubscribe(reader);
        }
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
