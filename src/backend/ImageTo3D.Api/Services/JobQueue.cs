using System.Threading.Channels;

namespace ImageTo3D.Api.Services;

/// <summary>
/// In-memory signal of job ids to process. The database is the source of truth; on startup
/// pending jobs are re-enqueued, so losing this queue on restart loses no work.
/// </summary>
public sealed class JobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}
