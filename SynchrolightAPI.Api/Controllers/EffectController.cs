using Microsoft.AspNetCore.Mvc;
using SynchrolightAPI.Api.Models;
using SynchrolightAPI.Api.Services;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Api.Controllers;

[ApiController]
[Route("api/effect")]
public class EffectController(EffectRunnerService runner, SequenceRecorder recorder) : ControllerBase
{
    // POST /api/effect/start
    [HttpPost("start")]
    public IActionResult Start([FromBody] StartEffectRequest req)
    {
        var effectParams = new EffectParams(
            Type: req.Type,
            Color: req.Color.ToRgb(),
            Field: (byte)(req.Field ?? 0x00),
            CycleDuration: req.CycleDurationMs.HasValue
                ? TimeSpan.FromMilliseconds(req.CycleDurationMs.Value)
                : null,
            FlashInterval: req.FlashIntervalMs.HasValue
                ? TimeSpan.FromMilliseconds(req.FlashIntervalMs.Value)
                : null,
            FadeSteps: req.FadeSteps ?? 20,
            Continuous: req.Continuous ?? true
        );

        recorder.RecordEffectStart(
            req.Type,
            effectParams.Color.R, effectParams.Color.G, effectParams.Color.B,
            effectParams.Field,
            req.CycleDurationMs,
            req.FadeSteps);

        runner.StartEffect(effectParams);

        return Ok(new ApiResponse(true,
            Message: $"Effect started: {req.Type}"));
    }

    // POST /api/effect/fade — 色→色のスムーズ遷移（サーバ側 ~50fps 補間 + 完了後ホールド）
    [HttpPost("fade")]
    public IActionResult Fade([FromBody] FadeRequest req)
    {
        runner.StartLinearFade(
            (byte)(req.Field ?? 0x00),
            req.From.ToRgb(),
            req.To.ToRgb(),
            req.DurationMs,
            req.FadeSteps ?? 20);

        return Ok(new ApiResponse(true,
            Message: $"Fade {req.DurationMs}ms"));
    }

    // POST /api/effect/stop
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        recorder.RecordEffectStop();
        runner.StopEffect();
        return Ok(new ApiResponse(true, Message: "Effect stopped"));
    }

    // GET /api/effect/status
    [HttpGet("status")]
    public IActionResult Status()
    {
        var (isRunning, current) = runner.GetEffectStatus();
        return Ok(new ApiResponse(true, Data: new
        {
            isRunning,
            effectType = current?.Type.ToString(),
            color = current != null
                ? new { current.Color.R, current.Color.G, current.Color.B }
                : null
        }));
    }
}
