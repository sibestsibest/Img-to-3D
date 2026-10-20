namespace ImageTo3D.Api.Services.Storage;

/// <summary>
/// Fixed set of artifact kinds. File names are derived from the kind only, never from user
/// input, which rules out path traversal by construction.
/// </summary>
public enum ArtifactKind
{
    /// <summary>The uploaded bytes, unchanged.</summary>
    Original,

    /// <summary>Upright (EXIF orientation applied) PNG sent to inference.</summary>
    SourceImage,

    /// <summary>Background-removed, re-framed image TripoSR actually saw.</summary>
    PreprocessedImage,

    /// <summary>Mesh as produced by inference, in model units.</summary>
    RawMesh,

    /// <summary>Scaled GLB for preview/download (metres, per glTF).</summary>
    Glb,

    /// <summary>Scaled binary STL (millimetres, Z-up).</summary>
    Stl,
}

public static class ArtifactNames
{
    public static string FileName(ArtifactKind kind, string? originalExtension = null) => kind switch
    {
        ArtifactKind.Original => "original" + (originalExtension is "png" or "jpg" or "webp" ? "." + originalExtension : ".bin"),
        ArtifactKind.SourceImage => "source.png",
        ArtifactKind.PreprocessedImage => "input.png",
        ArtifactKind.RawMesh => "raw.glb",
        ArtifactKind.Glb => "model.glb",
        ArtifactKind.Stl => "model.stl",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Most extra photos (other sides of the object) a job can carry.</summary>
    public const int MaxExtraImages = 5;

    /// <summary>Upright PNG of extra photo <paramref name="index"/> (1-based); derived from the index only.</summary>
    public static string ExtraImage(int index) =>
        index is >= 1 and <= MaxExtraImages ? $"extra-{index}.png" : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>JSON list of the extra photos' view hints, in upload order.</summary>
    public const string ExtraViews = "extras.json";
}

/// <summary>Artifact persistence. Local filesystem today; designed so an Azure Blob implementation can replace it.</summary>
public interface IArtifactStore
{
    Task SaveAsync(Guid jobId, string fileName, byte[] content, CancellationToken cancellationToken);

    /// <summary>Returns null when the artifact does not exist.</summary>
    Task<Stream?> OpenReadAsync(Guid jobId, string fileName, CancellationToken cancellationToken);

    Task<byte[]?> ReadAllAsync(Guid jobId, string fileName, CancellationToken cancellationToken);

    Task DeleteAsync(Guid jobId, string fileName, CancellationToken cancellationToken);

    Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Lists job folders with their last modification time (used for orphan cleanup).</summary>
    IAsyncEnumerable<(Guid JobId, DateTime LastModifiedUtc)> ListJobsAsync(CancellationToken cancellationToken);
}
