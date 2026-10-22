using System.Security.Cryptography;
using System.Text;
using ImageTo3D.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.Storage;

/// <summary>Fixed file names in a reference session folder; candidate files are derived from the candidate ID only.</summary>
public static class ReferenceArtifactNames
{
    /// <summary>Upright PNG of the input photo (from an upload or copied from a generation job).</summary>
    public const string InputImage = "input.png";

    public const string InputThumbnail = "inputthumb.jpg";

    public static string Thumbnail(Guid candidateId) => $"c{candidateId:N}.jpg";

    /// <summary>Manual uploads only: the upright full-size picture.</summary>
    public static string FullImage(Guid candidateId) => $"c{candidateId:N}full.png";

    /// <summary>Preview of a model candidate: the source's thumbnail, or a render of an uploaded mesh.</summary>
    public static string ModelThumbnail(Guid modelId) => $"m{modelId:N}.jpg";

    /// <summary>Uploaded model file, kept byte for byte; <paramref name="format"/> is "glb" or "stl".</summary>
    public static string ModelFile(Guid modelId, string format) =>
        format is "glb" or "stl" ? $"m{modelId:N}.{format}" : throw new ArgumentOutOfRangeException(nameof(format));
}

/// <summary>
/// Storage for reference sessions, one folder per session under
/// <see cref="StorageOptions.ReferenceRootPath"/>. Separate from job artifacts, so job retention
/// and orphan cleanup never touch it. Same name and path checks as the other stores.
/// </summary>
public interface IReferenceArtifactStore
{
    Task SaveAsync(Guid sessionId, string fileName, byte[] content, CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(Guid sessionId, string fileName, CancellationToken cancellationToken);

    Task<byte[]?> ReadAllAsync(Guid sessionId, string fileName, CancellationToken cancellationToken);

    Task DeleteAsync(Guid sessionId, string fileName, CancellationToken cancellationToken);

    Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken);
}

public sealed class LocalReferenceArtifactStore(IOptions<StorageOptions> options, IHostEnvironment environment) : IReferenceArtifactStore
{
    private readonly LocalFileArtifactStore _files = new(Path.GetFullPath(options.Value.ReferenceRootPath, environment.ContentRootPath));

    public Task SaveAsync(Guid sessionId, string fileName, byte[] content, CancellationToken cancellationToken) =>
        _files.SaveAsync(sessionId, fileName, content, cancellationToken);

    public Task<Stream?> OpenReadAsync(Guid sessionId, string fileName, CancellationToken cancellationToken) =>
        _files.OpenReadAsync(sessionId, fileName, cancellationToken);

    public Task<byte[]?> ReadAllAsync(Guid sessionId, string fileName, CancellationToken cancellationToken) =>
        _files.ReadAllAsync(sessionId, fileName, cancellationToken);

    public Task DeleteAsync(Guid sessionId, string fileName, CancellationToken cancellationToken) =>
        _files.DeleteAsync(sessionId, fileName, cancellationToken);

    public Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _files.DeleteJobAsync(sessionId, cancellationToken);
}

/// <summary>
/// Disposable download cache keyed by the SHA-256 of the remote address. File names are hex
/// digests only (never derived from the address text), so no request can escape the folder.
/// Sessions keep their own thumbnails, so clearing this loses nothing.
/// </summary>
public sealed class ReferenceDownloadCache
{
    private readonly string _root;

    public ReferenceDownloadCache(IOptions<StorageOptions> options, IHostEnvironment environment)
    {
        _root = Path.GetFullPath(options.Value.ReferenceCachePath, environment.ContentRootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<byte[]?> TryReadAsync(string url, CancellationToken cancellationToken)
    {
        var path = PathFor(url);
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        }
        catch (IOException)
        {
            return null; // being written or cleared concurrently: just download again
        }
    }

    public async Task WriteAsync(string url, byte[] content, CancellationToken cancellationToken)
    {
        var path = PathFor(url);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, content, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // The cache is best effort; a failed write only means a later download.
            File.Delete(temp);
        }
    }

    /// <summary>Deletes every cached download; returns how many files and bytes were removed.</summary>
    public (int Files, long Bytes) Clear()
    {
        int files = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles())
        {
            try
            {
                var length = file.Length;
                file.Delete();
                files++;
                bytes += length;
            }
            catch (IOException)
            {
            }
        }

        return (files, bytes);
    }

    private string PathFor(string url) =>
        Path.Combine(_root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".bin");
}
