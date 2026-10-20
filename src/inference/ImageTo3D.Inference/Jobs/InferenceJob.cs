using System.Collections.Concurrent;
using ImageTo3D.Core.Contracts;
using ImageTo3D.Inference.Pipeline;

namespace ImageTo3D.Inference.Jobs;

public sealed class InferenceJob(Guid id, string directory, GenerationRequest request)
{
    private readonly object _gate = new();

    public Guid Id { get; } = id;

    public string Directory { get; } = directory;

    /// <summary>Request payload; released once the job starts to free the image bytes.</summary>
    public GenerationRequest? Request { get; private set; } = request;

    public CancellationTokenSource Cancellation { get; } = new();

    public InferenceJobStatus Status { get; private set; } = InferenceJobStatus.Queued;

    public PipelineStage Stage { get; private set; } = PipelineStage.Queued;

    public double? StageProgress { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public InferenceMeshDto? Mesh { get; private set; }

    public bool IsTerminal => Status is InferenceJobStatus.Succeeded or InferenceJobStatus.Failed or InferenceJobStatus.Cancelled;

    /// <summary>Moves Queued -> Running. Returns false if the job was cancelled while queued.</summary>
    public bool TryStart()
    {
        lock (_gate)
        {
            if (Status != InferenceJobStatus.Queued)
                return false;
            Status = InferenceJobStatus.Running;
            StartedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public GenerationRequest TakeRequest()
    {
        lock (_gate)
        {
            var request = Request ?? throw new InvalidOperationException("Request already consumed.");
            Request = null;
            return request;
        }
    }

    public void ReportProgress(StageProgress progress)
    {
        lock (_gate)
        {
            if (IsTerminal)
                return;
            Stage = progress.Stage;
            StageProgress = progress.Fraction;
        }
    }

    public void Succeed(InferenceMeshDto mesh)
    {
        lock (_gate)
        {
            Mesh = mesh;
            Status = InferenceJobStatus.Succeeded;
            StageProgress = null;
            CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(string code, string message)
    {
        lock (_gate)
        {
            if (IsTerminal)
                return;
            Status = code == InferenceErrorCodes.Cancelled ? InferenceJobStatus.Cancelled : InferenceJobStatus.Failed;
            ErrorCode = code;
            Error = message;
            StageProgress = null;
            CompletedAt = DateTimeOffset.UtcNow;
            Request = null;
        }
    }

    /// <summary>Requests cancellation. Queued jobs are cancelled immediately; running jobs stop at the next checkpoint.</summary>
    public bool RequestCancel()
    {
        lock (_gate)
        {
            if (IsTerminal)
                return false;
            if (Status == InferenceJobStatus.Queued)
                Fail(InferenceErrorCodes.Cancelled, "Cancelled before processing started.");
        }

        Cancellation.Cancel();
        return true;
    }

    public InferenceJobDto ToDto()
    {
        lock (_gate)
        {
            var artifacts = new List<InferenceArtifactDto>();
            if (Status == InferenceJobStatus.Succeeded)
            {
                foreach (var name in InferenceArtifactNames.All)
                {
                    var path = Path.Combine(Directory, name);
                    if (File.Exists(path))
                        artifacts.Add(new InferenceArtifactDto(name, ContentTypeFor(name), new FileInfo(path).Length));
                }
            }

            return new InferenceJobDto(Id, Status, Stage.ToString(), StageProgress, ErrorCode, Error, CreatedAt, StartedAt, CompletedAt, artifacts, Mesh);
        }
    }

    public static string ContentTypeFor(string name) => name.EndsWith(".glb", StringComparison.Ordinal) ? "model/gltf-binary" : "image/png";
}

public sealed class InferenceJobStore
{
    private readonly ConcurrentDictionary<Guid, InferenceJob> _jobs = new();

    public void Add(InferenceJob job) => _jobs[job.Id] = job;

    public InferenceJob? Get(Guid id) => _jobs.GetValueOrDefault(id);

    public bool Remove(Guid id, out InferenceJob? job) => _jobs.TryRemove(id, out job);

    public IReadOnlyCollection<InferenceJob> All => _jobs.Values.ToArray();

    public int QueuedCount => _jobs.Values.Count(j => j.Status == InferenceJobStatus.Queued);
}
