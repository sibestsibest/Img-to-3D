using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Photogrammetry;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Api.Services.PartDesign;

/// <summary>Projects made from a photogrammetry result (Meshroom mesh at the job's current scale).</summary>
public sealed partial class PartDesignService
{
    public async Task<PartDesignProjectDto> CreateFromPhotogrammetryAsync(PhotogrammetryService photogrammetry, CreatePhotogrammetryDesignRequest request, CancellationToken cancellationToken)
    {
        await EnsureProjectCapacityAsync(cancellationToken);
        var (raw, mmPerUnit, job) = await photogrammetry.ResultForDesignAsync(request.JobId!.Value, cancellationToken);
        IReadOnlyList<GlbNodeMesh> nodes;
        try
        {
            nodes = GlbReader.ReadNodes(raw);
        }
        catch (InvalidDataException ex)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "unsupported_source", "The reconstructed mesh cannot be read: " + ex.Message);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var project = new PartDesignProject
        {
            Id = Guid.NewGuid(),
            OwnerKeyHash = owner.OwnerKeyHash,
            Name = Truncate(Clean(request.Name) ?? job.Name, 100),
            SourceKind = DesignSourceKinds.Photogrammetry,
            SourcePhotogrammetryJobId = job.Id,
            SourceFileName = "photogrammetry.glb",
            MmPerModelUnit = mmPerUnit,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var calibrated = job.ScaleStatus == PhotogrammetryScaleStatuses.Calibrated;
        var issues = new List<GeometryIssue>
        {
            calibrated
                ? new("scale_calibrated", IssueSeverity.Info, $"Scale calibrated on the photogrammetry job ({job.ScaleAxis} = {job.ScaleTargetMm:0.##} mm). Other dimensions follow from the reconstruction and are only as accurate as it is.")
                : new("scale_uncalibrated", IssueSeverity.Warning, "The photogrammetry result is not calibrated: its longest side was set to a default size, not the real one. Calibrate the job's scale with one measured dimension, then create the project again."),
            new("photogrammetry_mesh", IssueSeverity.Info, "Reconstructed by Meshroom from photos: surfaces the photos did not see are missing or filled, and the mesh is often not closed."),
        };
        await AnalyseAndSaveAsync(project, nodes, (DesignArtifactNames.SourceMesh, raw), issues, cancellationToken);
        logger.LogInformation("Created part design {ProjectId} from photogrammetry job {JobId} with {PartCount} parts", project.Id, job.Id, project.Parts.Count);
        return ToDto(project);
    }
}
