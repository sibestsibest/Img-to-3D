using System.Collections.Concurrent;
using System.Net;
using ImageTo3D.Api.Services.Inference;
using ImageTo3D.Core.Contracts;
using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Api.Tests.Infrastructure;

public enum FakeMode
{
    Succeed,
    Fail,
    Unavailable,
    Rejected,
    Hang,
    Lost,
    InvalidMesh,

    /// <summary>Succeeds with the sample box minus its top face (an open surface).</summary>
    OpenMesh,

    /// <summary>Succeeds with a flat plate, 1.0 x 0.02 x 0.5 model units (100 x 2 x 50 mm after the default scale).</summary>
    Plate,
}

/// <summary>In-memory stand-in for the inference service so backend tests need no model or GPU.</summary>
public sealed class FakeInferenceClient : IInferenceClient
{
    private readonly ConcurrentDictionary<Guid, (FakeMode Mode, int Polls, bool Cancelled)> _jobs = new();

    public FakeMode Mode { get; set; } = FakeMode.Succeed;

    public int Submissions;

    public ConcurrentBag<Guid> Deleted { get; } = [];

    public ConcurrentBag<Guid> CancelRequests { get; } = [];

    /// <summary>Raw mesh returned on success: a 0.5 x 1.0 x 0.25 box in model units (Y-up).</summary>
    public static byte[] SampleGlb(bool open = false)
    {
        float[] p =
        [
            0, 0, 0, 0.5f, 0, 0, 0.5f, 1, 0, 0, 1, 0,
            0, 0, 0.25f, 0.5f, 0, 0.25f, 0.5f, 1, 0.25f, 0, 1, 0.25f,
        ];
        int[] i = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 6, 2, 3, 7, 6, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5];
        // The y = 1 face (two triangles at offset 18) is the top in model units (Y-up).
        return GlbWriter.Write(new Mesh(p, open ? [.. i[..18], .. i[24..]] : i));
    }

    public static byte[] PlateGlb()
    {
        float[] p =
        [
            0, 0, 0, 1, 0, 0, 1, 0.02f, 0, 0, 0.02f, 0,
            0, 0, 0.5f, 1, 0, 0.5f, 1, 0.02f, 0.5f, 0, 0.02f, 0.5f,
        ];
        int[] i = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 6, 2, 3, 7, 6, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5];
        return GlbWriter.Write(new Mesh(p, i));
    }

    public Task<InferenceHealthDto?> GetHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult<InferenceHealthDto?>(Mode == FakeMode.Unavailable ? null : new InferenceHealthDto("ok", "Ready", null, "Cpu", 0));

    public Task<InferenceJobDto> SubmitAsync(byte[] png, string correlationId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Submissions);
        switch (Mode)
        {
            case FakeMode.Unavailable:
                throw new InferenceUnavailableException("The 3D generation service is unavailable. Please try again later.");
            case FakeMode.Rejected:
                throw new InferenceRequestException(HttpStatusCode.BadRequest, InferenceErrorCodes.InvalidImage, "Upload a PNG, JPEG or WebP image.");
        }

        var id = Guid.NewGuid();
        _jobs[id] = (Mode, 0, false);
        return Task.FromResult(Dto(id, InferenceJobStatus.Queued, "Queued"));
    }

    public Task<InferenceJobDto?> GetJobAsync(Guid inferenceJobId, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(inferenceJobId, out var state) || state.Mode == FakeMode.Lost)
            return Task.FromResult<InferenceJobDto?>(null);

        state.Polls++;
        _jobs[inferenceJobId] = state;

        if (state.Cancelled)
            return Task.FromResult<InferenceJobDto?>(Dto(inferenceJobId, InferenceJobStatus.Cancelled, "EncodingImage", InferenceErrorCodes.Cancelled, "Generation was cancelled."));
        if (state.Polls < 2 || state.Mode == FakeMode.Hang)
            return Task.FromResult<InferenceJobDto?>(Dto(inferenceJobId, InferenceJobStatus.Running, "EvaluatingDensity", progress: 0.5));

        return Task.FromResult<InferenceJobDto?>(state.Mode switch
        {
            FakeMode.Fail => Dto(inferenceJobId, InferenceJobStatus.Failed, "PreparingImage", InferenceErrorCodes.NoForeground, "No foreground object was detected in the image."),
            _ => Dto(inferenceJobId, InferenceJobStatus.Succeeded, "Exporting"),
        });
    }

    public Task<byte[]> DownloadArtifactAsync(Guid inferenceJobId, string name, CancellationToken cancellationToken)
    {
        var mode = _jobs[inferenceJobId].Mode;
        if (name == InferenceArtifactNames.Mesh)
            return Task.FromResult(mode switch
            {
                FakeMode.InvalidMesh => "glTF-not-really"u8.ToArray(),
                FakeMode.OpenMesh => SampleGlb(open: true),
                FakeMode.Plate => PlateGlb(),
                _ => SampleGlb(),
            });
        return Task.FromResult(TestImages.Png(64, 64));
    }

    public Task CancelAsync(Guid inferenceJobId, CancellationToken cancellationToken)
    {
        CancelRequests.Add(inferenceJobId);
        if (_jobs.TryGetValue(inferenceJobId, out var state))
            _jobs[inferenceJobId] = state with { Cancelled = true };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid inferenceJobId, CancellationToken cancellationToken)
    {
        Deleted.Add(inferenceJobId);
        return Task.CompletedTask;
    }

    private static InferenceJobDto Dto(Guid id, InferenceJobStatus status, string stage, string? code = null, string? error = null, double? progress = null) =>
        new(id, status, stage, progress, code, error, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, [], null);
}
