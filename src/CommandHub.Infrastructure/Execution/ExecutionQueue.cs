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

public sealed class LiveExecutionRegistry : ILiveExecutionRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, LiveExecutionHandle> _handles = new();

    public LiveExecutionHandle Register(Guid executionId, Guid serverId, string providerName, CancellationToken applicationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        var handle = new LiveExecutionHandle(executionId, serverId, providerName, null, source, DateTimeOffset.UtcNow, Guid.NewGuid());
        if (!_handles.TryAdd(executionId, handle))
        {
            source.Dispose();
            throw new InvalidOperationException("执行已在实时注册表中。");
        }
        return handle;
    }

    public bool TryGet(Guid executionId, out LiveExecutionHandle? handle) => _handles.TryGetValue(executionId, out handle);

    public bool TrySetRemoteProcessGroupId(Guid executionId, Guid nonce, long processGroupId)
    {
        if (processGroupId <= 0 || !_handles.TryGetValue(executionId, out var current) || current.ExecutionNonce != nonce) return false;
        return _handles.TryUpdate(executionId, current with { RemoteProcessGroupId = processGroupId }, current);
    }

    public bool RequestCancellation(Guid executionId)
    {
        if (!_handles.TryGetValue(executionId, out var handle)) return false;
        handle.CancellationTokenSource.Cancel();
        return true;
    }

    public bool Remove(Guid executionId, Guid nonce)
    {
        if (!_handles.TryGetValue(executionId, out var current) || current.ExecutionNonce != nonce) return false;
        if (!_handles.TryRemove(new KeyValuePair<Guid, LiveExecutionHandle>(executionId, current))) return false;
        current.CancellationTokenSource.Dispose();
        return true;
    }
}
