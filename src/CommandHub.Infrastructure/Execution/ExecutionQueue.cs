using System.Threading.Channels;
using CommandHub.Application;
using Microsoft.Extensions.Options;

namespace CommandHub.Infrastructure.Execution;

public sealed class ExecutionQueue : IExecutionQueue
{
    private readonly Channel<ExecutionQueueItem> _channel;

    public ExecutionQueue(IOptions<ExecutionOptions> options)
    {
        _channel = Channel.CreateBounded<ExecutionQueueItem>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public ValueTask<bool> TryEnqueueAsync(ExecutionQueueItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_channel.Writer.TryWrite(item));
    }

    public IAsyncEnumerable<ExecutionQueueItem> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
    public void Complete() => _channel.Writer.TryComplete();
}

public sealed class ExecutionCancellationRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public CancellationToken Register(Guid executionId, CancellationToken applicationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        if (!_sources.TryAdd(executionId, source)) source.Dispose();
        return _sources[executionId].Token;
    }

    public bool Cancel(Guid executionId)
    {
        if (!_sources.TryGetValue(executionId, out var source)) return false;
        source.Cancel();
        return true;
    }

    public void Remove(Guid executionId)
    {
        if (_sources.TryRemove(executionId, out var source)) source.Dispose();
    }
}
