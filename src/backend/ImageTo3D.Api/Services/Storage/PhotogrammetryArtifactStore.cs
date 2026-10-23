using ImageTo3D.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.Storage;

/// <summary>File names inside a photogrammetry job folder (flat, generated, never user input).</summary>
public static class PhotogrammetryArtifactNames
{
    public static string Photo(Guid photoId, string extension) => $"p{photoId:N}.{extension}";

    public static string Thumbnail(Guid photoId) => $"t{photoId:N}.jpg";

    /// <summary>Meshroom's mesh as read back, in its own units.</summary>
    public const string RawMesh = "raw.glb";

    /// <summary>Scaled result (metres, as glTF requires).</summary>
    public const string Glb = "model.glb";

    /// <summary>Scaled result in millimetres, Z-up.</summary>
    public const string Stl = "model.stl";

    /// <summary>Everything Meshroom wrote to its output folder (textured OBJ, MTL, textures) plus the log.</summary>
    public const string OutputZip = "result.zip";

    public const string Log = "meshroom.log";
}

/// <summary>
/// Storage for photogrammetry jobs, one folder per job under
/// <see cref="StorageOptions.PhotogrammetryRootPath"/>; job retention never touches it.
/// </summary>
public interface IPhotogrammetryArtifactStore
{
    Task SaveAsync(Guid jobId, string fileName, byte[] content, CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(Guid jobId, string fileName, CancellationToken cancellationToken);

    Task<byte[]?> ReadAllAsync(Guid jobId, string fileName, CancellationToken cancellationToken);

    Task DeleteAsync(Guid jobId, string fileName, CancellationToken cancellationToken);

    Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed class LocalPhotogrammetryArtifactStore(IOptions<StorageOptions> options, IHostEnvironment environment) : IPhotogrammetryArtifactStore
{
    private readonly LocalFileArtifactStore _files = new(Path.GetFullPath(options.Value.PhotogrammetryRootPath, environment.ContentRootPath));

    public Task SaveAsync(Guid jobId, string fileName, byte[] content, CancellationToken cancellationToken) =>
        _files.SaveAsync(jobId, fileName, content, cancellationToken);

    public Task<Stream?> OpenReadAsync(Guid jobId, string fileName, CancellationToken cancellationToken) =>
        _files.OpenReadAsync(jobId, fileName, cancellationToken);

    public Task<byte[]?> ReadAllAsync(Guid jobId, string fileName, CancellationToken cancellationToken) =>
        _files.ReadAllAsync(jobId, fileName, cancellationToken);

    public Task DeleteAsync(Guid jobId, string fileName, CancellationToken cancellationToken) =>
        _files.DeleteAsync(jobId, fileName, cancellationToken);

    public Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        _files.DeleteJobAsync(jobId, cancellationToken);
}
