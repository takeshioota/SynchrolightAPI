using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Api.Services;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/sequence")]
public class SequenceController(EffectRunnerService runner, SequenceStore store, SequenceRecorder recorder) : ControllerBase
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

    // POST /api/sequence/play/inline — エディタ内容をインライン再生
    [HttpPost("play/inline")]
    public IActionResult PlayInline([FromBody] PlayInlineRequest req)
    {
        if (req.Steps == null || req.Steps.Count == 0)
            return BadRequest(new ApiResponse(false, Error: "Steps are required"));

        var sequence = new SynchrolightAPI.Models.Sequence
        {
            Name = "inline",
            Steps = req.Steps
        };

        runner.StartSequenceInline(sequence, loop: req.Loop ?? false);
        return Ok(new ApiResponse(true, Message: $"Inline playback started ({req.Steps.Count} steps, loop={req.Loop ?? false})"));
    }

    // POST /api/sequence/play/step — 単一ステップ即時実行
    [HttpPost("play/step")]
    public async Task<IActionResult> PlayStep([FromBody] PlayStepRequest req)
    {
        if (req.Step == null)
            return BadRequest(new ApiResponse(false, Error: "Step is required"));

        await runner.PlaySingleStepAsync(req.Step);
        return Ok(new ApiResponse(true, Message: $"Step executed: {req.Step.CommandType}"));
    }

    // POST /api/sequence/play/jump — 再生中にジャンプ
    [HttpPost("play/jump")]
    public IActionResult PlayJump([FromBody] JumpToStepRequest req)
    {
        if (!runner.JumpToStep(req.StepIndex))
            return BadRequest(new ApiResponse(false, Error: "Jump failed: no inline sequence or invalid index"));

        return Ok(new ApiResponse(true, Message: $"Jumped to step {req.StepIndex}"));
    }

    // GET /api/sequence/play/status — 再生状態取得
    [HttpGet("play/status")]
    public IActionResult PlayStatus()
    {
        var (isPlaying, name, currentStepIndex, totalStepCount) = runner.GetSequenceStatus();
        return Ok(new ApiResponse(true, Data: new
        {
            isPlaying,
            sequenceName = name,
            currentStepIndex,
            totalStepCount
        }));
    }

    // =========================================================
    //  シーケンス記録
    // =========================================================

    // POST /api/sequence/record/start — 記録開始
    [HttpPost("record/start")]
    public IActionResult RecordStart()
    {
        recorder.Start();
        return Ok(new ApiResponse(true, Message: "記録を開始しました"));
    }

    // POST /api/sequence/record/stop — 記録停止 → ステップリスト返却
    [HttpPost("record/stop")]
    public IActionResult RecordStop()
    {
        var steps = recorder.Stop();
        var totalMs = steps.Count > 0 ? steps.Max(s => s.TimeOffsetMs) : 0;
        return Ok(new ApiResponse(true,
            Message: $"記録を停止しました ({steps.Count} ステップ / {totalMs / 1000.0:F1}秒)",
            Data: new { steps }));
    }

    // GET /api/sequence/record/status — 記録状態取得
    [HttpGet("record/status")]
    public IActionResult RecordStatus()
    {
        var (isRecording, stepCount, elapsedMs) = recorder.GetStatus();
        return Ok(new ApiResponse(true, Data: new
        {
            isRecording,
            stepCount,
            elapsedMs
        }));
    }
}
