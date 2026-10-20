using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ImageTo3D.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.Storage;

public sealed partial class LocalFileArtifactStore : IArtifactStore
{
    private readonly string _root;

    public LocalFileArtifactStore(IOptions<StorageOptions> options, IHostEnvironment environment)
    {
        _root = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(Guid jobId, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var path = ResolvePath(jobId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write-then-rename so readers never observe a partially written artifact.
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temp, content, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    public Task<Stream?> OpenReadAsync(Guid jobId, string fileName, CancellationToken cancellationToken)
    {
        var path = ResolvePath(jobId, fileName);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public async Task<byte[]?> ReadAllAsync(Guid jobId, string fileName, CancellationToken cancellationToken)
    {
        var path = ResolvePath(jobId, fileName);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public Task DeleteAsync(Guid jobId, string fileName, CancellationToken cancellationToken)
    {
        var path = ResolvePath(jobId, fileName);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var dir = JobDirectory(jobId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(Guid JobId, DateTime LastModifiedUtc)> ListJobsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParseExact(Path.GetFileName(dir), "N", out var id))
                yield return (id, Directory.GetLastWriteTimeUtc(dir));
        }

        await Task.CompletedTask;
    }

    private string JobDirectory(Guid jobId) => Path.Combine(_root, jobId.ToString("N"));

    private string ResolvePath(Guid jobId, string fileName)
    {
        // Defence in depth: names come from ArtifactNames, but enforce a strict pattern anyway
        // and verify the resolved path stays inside the job folder.
        if (!SafeFileName().IsMatch(fileName))
            throw new ArgumentException("Invalid artifact name.", nameof(fileName));

        var dir = JobDirectory(jobId);
        var full = Path.GetFullPath(Path.Combine(dir, fileName));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Invalid artifact path.", nameof(fileName));
        return full;
    }

    [GeneratedRegex("^[a-z0-9]+\\.[a-z0-9]{2,4}$")]
    private static partial Regex SafeFileName();
}
