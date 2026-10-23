using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Inference;
using ImageTo3D.Api.Services.Parametric;
using Microsoft.AspNetCore.Mvc;

namespace ImageTo3D.Api.Controllers;

/// <summary>
/// Which geometry engines can be used right now, so the UI can offer only working workflows and
/// say why the others are missing. Exposes no paths, configuration or secrets.
/// </summary>
[ApiController]
[Route("api/capabilities")]
public sealed class CapabilitiesController(IParametricWorker parametric, IInferenceClient inference) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<CapabilitiesDto>(StatusCodes.Status200OK)]
    public async Task<CapabilitiesDto> Get(CancellationToken cancellationToken)
    {
        var p = await parametric.GetCapabilityAsync(cancellationToken);
        var engines = new List<EngineCapabilityDto>
        {
            new("parametric", "parametric", GeometryOrigins.Parametric, p.Status, p.EngineVersion, p.Reason),
            await SingleImageAsync(cancellationToken),
            new("meshroom", "photogrammetry", "photogrammetry", "unavailable", null, "Multi-photo reconstruction (Meshroom) is not integrated yet."),
        };
        return new CapabilitiesDto(engines);
    }

    private async Task<EngineCapabilityDto> SingleImageAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var health = await inference.GetHealthAsync(timeout.Token);
            if (health is null)
                return new("single_image", "single_image_ai", GeometryOrigins.AiPredicted, "unavailable", null, "The inference service is not reachable; start it with scripts/dev.ps1.");
            var ready = health.Status == "ok";
            return new(health.Engine ?? "single_image", "single_image_ai", GeometryOrigins.AiPredicted, ready ? "ready" : "unavailable", null,
                ready ? null : $"The inference service reports '{health.Status}' (models {health.ModelState}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("single_image", "single_image_ai", GeometryOrigins.AiPredicted, "unavailable", null, "The inference service did not answer in time.");
        }
    }
}
