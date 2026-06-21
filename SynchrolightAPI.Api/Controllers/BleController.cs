using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/ble")]
public class BleController(IBleTransport ble, BleFileTransferService transfer) : ControllerBase
{
    // GET /api/ble/scan — BLELightデバイスのスキャン
    [HttpGet("scan")]
    public async Task<IActionResult> Scan([FromQuery] int timeout = 5, CancellationToken ct = default)
    {
        var devices = await ble.ScanAsync(timeout, ct);
        return Ok(new ApiResponse(true,
            Message: $"Found {devices.Count} device(s)",
            Data: new { devices }));
    }

    // POST /api/ble/connect — デバイスに接続
    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] BleConnectRequest req, CancellationToken ct)
    {
        await ble.ConnectAsync(req.DeviceAddress, ct);
        return Ok(new ApiResponse(true,
            Message: $"Connected to {req.DeviceAddress}"));
    }

    // POST /api/ble/disconnect — 切断
    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await ble.DisconnectAsync();
        return Ok(new ApiResponse(true, Message: "Disconnected"));
    }

    // GET /api/ble/status — 接続状態
    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new ApiResponse(true, Data: new
        {
            connected = ble.IsConnected,
            deviceAddress = ble.ConnectedDeviceAddress,
            transferring = transfer.IsTransferring,
            progress = transfer.IsTransferring
                ? new { current = transfer.Progress.Current, total = transfer.Progress.Total }
                : null
        }));
    }

    // POST /api/ble/file-write — BLE経由ファイル転送
    [HttpPost("file-write")]
    public async Task<IActionResult> FileWrite([FromBody] BleFileWriteRequest req, CancellationToken ct)
    {
        if (!ble.IsConnected)
            return BadRequest(new ApiResponse(false, Error: "BLEデバイスに接続されていません"));

        byte[] data;
        try
        {
            data = Convert.FromBase64String(req.Data);
        }
        catch (FormatException)
        {
            return BadRequest(new ApiResponse(false, Error: "Invalid base64 data"));
        }

        if (data.Length == 0)
            return BadRequest(new ApiResponse(false, Error: "Data is empty"));

        await transfer.TransferFileAsync(data, ct: ct);
        return Ok(new ApiResponse(true,
            Message: $"BLE file transfer complete: {data.Length}bytes, {transfer.Progress.Total} segments"));
    }

    // GET /api/ble/file-write/progress — 転送進捗
    [HttpGet("file-write/progress")]
    public IActionResult FileWriteProgress()
    {
        return Ok(new ApiResponse(true, Data: new
        {
            transferring = transfer.IsTransferring,
            current = transfer.Progress.Current,
            total = transfer.Progress.Total
        }));
    }
}

// Request DTOs
public record BleConnectRequest(string DeviceAddress);
public record BleFileWriteRequest(string Data);  // Base64エンコード
