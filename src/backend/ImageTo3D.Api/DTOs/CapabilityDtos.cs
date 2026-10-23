namespace ImageTo3D.Api.DTOs;

/// <param name="Id">Engine identifier: <c>parametric</c>, the inference engine's name (TripoSR, StableFast3D, SPAR3D) or <c>meshroom</c>.</param>
/// <param name="Kind"><c>parametric</c>, <c>single_image_ai</c> or <c>photogrammetry</c>.</param>
/// <param name="Origin">Geometry origin of its results: <c>parametric</c>, <c>ai_predicted</c> or <c>photogrammetry</c>.</param>
/// <param name="Status"><c>ready</c>, <c>missing</c> (not set up), <c>disabled</c>, <c>failed</c> or <c>unavailable</c>.</param>
/// <param name="Reason">Why it cannot be used, in words a user can act on; null when ready.</param>
public sealed record EngineCapabilityDto(string Id, string Kind, string Origin, string Status, string? Version, string? Reason);

public sealed record CapabilitiesDto(IReadOnlyList<EngineCapabilityDto> Engines);
