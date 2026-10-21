using ImageTo3D.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.Storage;

/// <summary>Fixed file names in a part-design project folder; part files are derived from the part ID only.</summary>
public static class DesignArtifactNames
{
    /// <summary>Byte-for-byte copy of the generation job's raw mesh (model units), never modified.</summary>
    public const string SourceMesh = "source.glb";

    public static string PartGeometry(Guid partId) => $"p{partId:N}.glb";

    /// <summary>Zip of a release, written once and never modified.</summary>
    public static string ReleasePackage(Guid releaseId) => $"r{releaseId:N}.zip";
}

/// <summary>
/// Artifact storage for part-design projects, one folder per project under
/// <see cref="StorageOptions.DesignRootPath"/>. A separate root from job artifacts, so the job
/// retention and orphan cleanup never see (or delete) design data. Same name and path checks.
/// </summary>
public interface IDesignArtifactStore
{
    Task SaveAsync(Guid projectId, string fileName, byte[] content, CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(Guid projectId, string fileName, CancellationToken cancellationToken);

    Task<byte[]?> ReadAllAsync(Guid projectId, string fileName, CancellationToken cancellationToken);

    Task DeleteAsync(Guid projectId, string fileName, CancellationToken cancellationToken);

    Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken);
}

public sealed class LocalDesignArtifactStore(IOptions<StorageOptions> options, IHostEnvironment environment) : IDesignArtifactStore
{
    private readonly LocalFileArtifactStore _files = new(Path.GetFullPath(options.Value.DesignRootPath, environment.ContentRootPath));

    public Task SaveAsync(Guid projectId, string fileName, byte[] content, CancellationToken cancellationToken) =>
        _files.SaveAsync(projectId, fileName, content, cancellationToken);

    public Task<Stream?> OpenReadAsync(Guid projectId, string fileName, CancellationToken cancellationToken) =>
        _files.OpenReadAsync(projectId, fileName, cancellationToken);

    public Task<byte[]?> ReadAllAsync(Guid projectId, string fileName, CancellationToken cancellationToken) =>
        _files.ReadAllAsync(projectId, fileName, cancellationToken);

    public Task DeleteAsync(Guid projectId, string fileName, CancellationToken cancellationToken) =>
        _files.DeleteAsync(projectId, fileName, cancellationToken);

    public Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        _files.DeleteJobAsync(projectId, cancellationToken);
}
