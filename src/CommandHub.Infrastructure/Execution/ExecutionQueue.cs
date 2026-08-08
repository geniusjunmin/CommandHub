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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Entry> _handles = new();

    public LiveExecutionHandle Register(Guid executionId, Guid serverId, string providerName, CancellationToken applicationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        var handle = new LiveExecutionHandle(executionId, serverId, providerName, null, source, DateTimeOffset.UtcNow, Guid.NewGuid());
        if (!_handles.TryAdd(executionId, new Entry(handle)))
        {
            source.Dispose();
            throw new InvalidOperationException("执行已在实时注册表中。");
        }
        return handle;
    }

    public bool TrySetRemoteProcessGroupId(Guid executionId, Guid nonce, long processGroupId)
    {
        if (processGroupId <= 0 || !_handles.TryGetValue(executionId, out var entry)) return false;
        entry.Gate.Wait();
        try
        {
            if (!entry.Active || entry.Handle.ExecutionNonce != nonce) return false;
            entry.Handle = entry.Handle with { RemoteProcessGroupId = processGroupId };
            return true;
        }
        finally { entry.Gate.Release(); }
    }

    public bool TryAcquireCancellationLease(Guid executionId, out ILiveExecutionCancellationLease? lease)
    {
        lease = null;
        if (!_handles.TryGetValue(executionId, out var entry)) return false;
        entry.Gate.Wait();
        try
        {
            if (!entry.Active || entry.CancellationStarted) return false;
            entry.CancellationStarted = true;
            lease = new CancellationLease(this, entry, entry.Handle);
            return true;
        }
        finally { entry.Gate.Release(); }
    }

    public bool Remove(Guid executionId, Guid nonce)
    {
        if (!_handles.TryGetValue(executionId, out var entry)) return false;
        entry.Gate.Wait();
        try
        {
            if (!entry.Active || entry.Handle.ExecutionNonce != nonce) return false;
            entry.Active = false;
            if (!_handles.TryRemove(new KeyValuePair<Guid, Entry>(executionId, entry))) return false;
            entry.Handle.CancellationTokenSource.Dispose();
            return true;
        }
        finally { entry.Gate.Release(); }
    }

    private bool IsValid(Entry entry, Guid nonce) =>
        _handles.TryGetValue(entry.Handle.ExecutionId, out var current)
        && ReferenceEquals(current, entry) && entry.Active && entry.Handle.ExecutionNonce == nonce;

    private sealed class Entry(LiveExecutionHandle handle)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public LiveExecutionHandle Handle { get; set; } = handle;
        public bool Active { get; set; } = true;
        public bool CancellationStarted { get; set; }
    }

    private sealed class CancellationLease(LiveExecutionRegistry owner, Entry entry, LiveExecutionHandle snapshot) : ILiveExecutionCancellationLease
    {
        public Guid ExecutionId => snapshot.ExecutionId;
        public Guid ServerId => snapshot.ServerId;
        public string ProviderName => snapshot.ProviderName;
        public Guid ExecutionNonce => snapshot.ExecutionNonce;
        public long? RemoteProcessGroupId => snapshot.RemoteProcessGroupId;
        public CancellationTokenSource CancellationTokenSource => snapshot.CancellationTokenSource;
        public bool IsValid
        {
            get
            {
                entry.Gate.Wait();
                try { return owner.IsValid(entry, snapshot.ExecutionNonce); }
                finally { entry.Gate.Release(); }
            }
        }
        public bool TryRequestLocalCancellation()
        {
            entry.Gate.Wait();
            try
            {
                if (!owner.IsValid(entry, snapshot.ExecutionNonce)) return false;
                snapshot.CancellationTokenSource.Cancel();
                return true;
            }
            finally { entry.Gate.Release(); }
        }
        public async ValueTask<IAsyncDisposable?> TryAcquireRemoteSignalLeaseAsync(CancellationToken cancellationToken)
        {
            await entry.Gate.WaitAsync(cancellationToken);
            if (!owner.IsValid(entry, snapshot.ExecutionNonce))
            {
                entry.Gate.Release();
                return null;
            }
            return new RemoteSignalLease(entry.Gate);
        }
        public void Dispose() { }
    }

    private sealed class RemoteSignalLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
