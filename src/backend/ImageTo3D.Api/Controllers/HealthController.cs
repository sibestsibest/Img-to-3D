using ImageTo3D.Api.Data;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Services.Inference;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ImageTo3D.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController(AppDbContext db, IInferenceClient inference) : ControllerBase
{
    /// <summary>Liveness plus dependency status. Exposes no configuration, paths or secrets.</summary>
    [HttpGet]
    [ProducesResponseType<HealthDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthDto>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var dbOk = await db.Database.CanConnectAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        InferenceHealthSummaryDto inferenceStatus;
        try
        {
            var health = await inference.GetHealthAsync(timeout.Token);
            inferenceStatus = health is null ? new("unreachable", null) : new(health.Status, health.ModelState, health.Engine);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            inferenceStatus = new("unreachable", null);
        }

        var status = !dbOk ? "unhealthy" : inferenceStatus.Status == "ok" ? "ok" : "degraded";
        var version = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "unknown";
        var dto = new HealthDto(status, dbOk ? "ok" : "unavailable", inferenceStatus, version);
        return dbOk ? Ok(dto) : StatusCode(StatusCodes.Status503ServiceUnavailable, dto);
    }
}
