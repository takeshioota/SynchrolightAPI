using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Api.Services;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/sequence")]
public class SequenceController(EffectRunnerService runner, SequenceStore store) : ControllerBase
{
    // GET /api/sequence — シーケンス一覧
    [HttpGet]
    public IActionResult List()
    {
        var names = store.ListNames();
        return Ok(new ApiResponse(true, Data: new { names }));
    }

    // GET /api/sequence/{name} — シーケンス取得
    [HttpGet("{name}")]
    public IActionResult Get(string name)
    {
        var sequence = store.Load(name);
        if (sequence == null)
            return NotFound(new ApiResponse(false, Error: $"Sequence not found: {name}"));

        return Ok(new ApiResponse(true, Data: sequence));
    }

    // POST /api/sequence — シーケンス保存
    [HttpPost]
    public IActionResult Save([FromBody] SaveSequenceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new ApiResponse(false, Error: "Name is required"));

        if (req.Steps == null || req.Steps.Count == 0)
            return BadRequest(new ApiResponse(false, Error: "Steps are required"));

        var sequence = new SynchrolightAPI.Models.Sequence
        {
            Name = req.Name,
            Steps = req.Steps
        };

        store.Save(sequence);
        return Ok(new ApiResponse(true, Message: $"Sequence saved: {req.Name}"));
    }

    // DELETE /api/sequence/{name} — シーケンス削除
    [HttpDelete("{name}")]
    public IActionResult Delete(string name)
    {
        if (!store.Delete(name))
            return NotFound(new ApiResponse(false, Error: $"Sequence not found: {name}"));

        return Ok(new ApiResponse(true, Message: $"Sequence deleted: {name}"));
    }

    // POST /api/sequence/play — シーケンス再生開始
    [HttpPost("play")]
    public IActionResult Play([FromBody] PlaySequenceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new ApiResponse(false, Error: "Name is required"));

        if (!runner.StartSequence(req.Name))
            return NotFound(new ApiResponse(false, Error: $"Sequence not found: {req.Name}"));

        return Ok(new ApiResponse(true, Message: $"Sequence playback started: {req.Name}"));
    }

    // POST /api/sequence/stop — シーケンス再生停止
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        runner.StopSequence();
        return Ok(new ApiResponse(true, Message: "Sequence playback stopped"));
    }

    // GET /api/sequence/play/status — 再生状態取得
    [HttpGet("play/status")]
    public IActionResult PlayStatus()
    {
        var (isPlaying, name) = runner.GetSequenceStatus();
        return Ok(new ApiResponse(true, Data: new
        {
            isPlaying,
            sequenceName = name
        }));
    }
}
