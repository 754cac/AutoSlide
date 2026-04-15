using System.Threading.Channels;

namespace BackendServer.Features.Classroom.Services;

// payload for a bake job
public class BakeJob
{
    public Guid SessionId { get; set; }
    public Guid CourseId { get; set; }
}

public interface IArtifactBakingQueue
{
    void QueueBakeJob(BakeJob job);
    Task<BakeJob> DequeueAsync(CancellationToken cancellationToken);
}

public class ArtifactBakingQueue : IArtifactBakingQueue
{
    private readonly Channel<BakeJob> _queue;

    public ArtifactBakingQueue()
    {
        var options = new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _queue = Channel.CreateBounded<BakeJob>(options);
    }

    public void QueueBakeJob(BakeJob job)
    {
        _queue.Writer.TryWrite(job);
    }

    public async Task<BakeJob> DequeueAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}