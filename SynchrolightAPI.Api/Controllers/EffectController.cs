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
