using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommandHub.Infrastructure.Execution;

public sealed class ExecutionWorkerService(
    IExecutionQueue queue,
    ICommandExecutionProvider provider,
    IDbContextFactory<CommandHubDbContext> dbFactory,
    IExecutionNotifier notifier,
    ICommandClassificationService classifier,
    ILiveExecutionRegistry liveRegistry,
    IOptions<ExecutionOptions> options,
    ILogger<ExecutionWorkerService> logger) : BackgroundService
{
    private readonly ExecutionOptions _options = options.Value;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _serverLimits = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userLimits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var global = new SemaphoreSlim(_options.GlobalConcurrency, _options.GlobalConcurrency);
        try
        {
            await foreach (var item in queue.ReadAllAsync(stoppingToken))
            {
                await global.WaitAsync(stoppingToken);
                var task = ProcessWithLimitsAsync(item, global, stoppingToken);
                _running[item.ExecutionId] = task;
                _ = task.ContinueWith(_ => _running.TryRemove(item.ExecutionId, out var ignoredTask), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            queue.Complete();
            await Task.WhenAll(_running.Values);
        }
    }

    private async Task ProcessWithLimitsAsync(ExecutionQueueItem item, SemaphoreSlim global, CancellationToken stoppingToken)
    {
        var serverLimit = _serverLimits.GetOrAdd(item.ServerId, _ => new(_options.PerServerConcurrency, _options.PerServerConcurrency));
        var userLimit = _userLimits.GetOrAdd(item.UserId, _ => new(_options.PerUserConcurrency, _options.PerUserConcurrency));
        await serverLimit.WaitAsync(stoppingToken);
        await userLimit.WaitAsync(stoppingToken);
        try { await ProcessAsync(item, stoppingToken); }
        finally { userLimit.Release(); serverLimit.Release(); global.Release(); }
    }

    private async Task ProcessAsync(ExecutionQueueItem item, CancellationToken stoppingToken)
    {
        var claimed = await ClaimQueuedExecutionAsync(item.ExecutionId, stoppingToken);
        if (!claimed) return;
        var handle = liveRegistry.Register(item.ExecutionId, item.ServerId, provider.ProviderName, stoppingToken);
        var cancellationToken = handle.CancellationTokenSource.Token;
        try
        {
            await notifier.ExecutionStartedAsync(item.ExecutionId, cancellationToken);
            using var sink = new DatabaseOutputSink(item.ExecutionId, handle.ExecutionNonce, dbFactory, notifier, liveRegistry, _options);
            var result = await provider.StartAsync(item, sink, cancellationToken);
            var status = result.TimedOut ? ExecutionStatus.TimedOut : result.ExitCode == 0 ? ExecutionStatus.Succeeded : ExecutionStatus.Failed;
            await CompleteAsync(item.ExecutionId, status, result.ExitCode, result.TimedOut, false, result.RemoteProcessId, CancellationToken.None);
            await AddAutomaticTagsAsync(item.ExecutionId, item.CommandText, CancellationToken.None);
            await notifier.ExecutionCompletedAsync(item.ExecutionId, status, result.ExitCode, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await CompleteAsync(item.ExecutionId, ExecutionStatus.Cancelled, null, false, true, null, CancellationToken.None);
            await notifier.ExecutionCompletedAsync(item.ExecutionId, ExecutionStatus.Cancelled, null, CancellationToken.None);
        }
        catch (HostKeyVerificationException exception)
        {
            logger.LogWarning("Execution {ExecutionId} rejected because SSH host key verification failed: {Reason}", item.ExecutionId, exception.Message);
            await CompleteAsync(item.ExecutionId, ExecutionStatus.ConnectionFailed, null, false, false, null, CancellationToken.None);
            await notifier.ExecutionCompletedAsync(item.ExecutionId, ExecutionStatus.ConnectionFailed, null, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Execution {ExecutionId} failed.", item.ExecutionId);
            await CompleteAsync(item.ExecutionId, ExecutionStatus.Failed, null, false, false, null, CancellationToken.None);
            await notifier.ExecutionCompletedAsync(item.ExecutionId, ExecutionStatus.Failed, null, CancellationToken.None);
        }
        finally { liveRegistry.Remove(item.ExecutionId, handle.ExecutionNonce); }
    }

    private async Task<bool> ClaimQueuedExecutionAsync(Guid executionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var updated = await db.CommandExecutions
            .Where(x => x.Id == executionId && x.Status == ExecutionStatus.Queued && x.CancellationRequestedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ExecutionStatus.Running)
                .SetProperty(x => x.StartedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        if (updated == 1) await notifier.StatusChangedAsync(executionId, ExecutionStatus.Running, cancellationToken);
        return updated == 1;
    }

    private async Task CompleteAsync(Guid executionId, ExecutionStatus status, int? exitCode, bool timedOut, bool cancelled, long? processId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var execution = await db.CommandExecutions.SingleAsync(x => x.Id == executionId, cancellationToken);
        execution.Status = status;
        execution.ExitCode = exitCode;
        execution.TimedOut = timedOut;
        execution.WasCancelled = cancelled;
        execution.RemoteProcessId ??= processId;
        execution.FinishedAt = DateTimeOffset.UtcNow;
        execution.DurationMilliseconds = execution.StartedAt is null ? null : (long)(execution.FinishedAt.Value - execution.StartedAt.Value).TotalMilliseconds;
        execution.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task AddAutomaticTagsAsync(Guid executionId, string command, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var name in classifier.Classify(command))
        {
            var normalized = name.ToUpperInvariant();
            var tag = await db.Tags.SingleOrDefaultAsync(x => x.NormalizedName == normalized, cancellationToken);
            if (tag is null) { tag = new Tag { Name = name, NormalizedName = normalized }; db.Tags.Add(tag); }
            if (!await db.CommandExecutionTags.AnyAsync(x => x.ExecutionId == executionId && x.TagId == tag.Id, cancellationToken))
                db.CommandExecutionTags.Add(new CommandExecutionTag { ExecutionId = executionId, Tag = tag, IsAutomatic = true });
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class DatabaseOutputSink(Guid executionId, Guid executionNonce, IDbContextFactory<CommandHubDbContext> dbFactory, IExecutionNotifier notifier, ILiveExecutionRegistry liveRegistry, ExecutionOptions options) : IExecutionOutputSink, IDisposable
{
    private static readonly Regex Ansi = new(@"[\u001B\u009B][[\]()#;?]*(?:(?:(?:[a-zA-Z\d]*(?:;[-a-zA-Z\d/#&.:=?%@~_]+)*)?\u0007)|(?:(?:\d{1,4}(?:;\d{0,4})*)?[\dA-PR-TZcf-nq-uy=><~]))", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _sequence;
    private long _totalBytes;
    private bool _truncationRecorded;

    public async Task WriteAsync(OutputStreamType streamType, string content, CancellationToken cancellationToken)
    {
        var safe = Ansi.Replace(content.Replace("\0", string.Empty, StringComparison.Ordinal), string.Empty);
        foreach (var chunk in SplitByUtf8Bytes(safe, options.OutputChunkBytes))
            await WriteChunkAsync(streamType, chunk, cancellationToken);
    }

    public async Task SetRemoteProcessIdAsync(long processId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (!liveRegistry.TrySetRemoteProcessGroupId(executionId, executionNonce, processId))
            throw new InvalidOperationException("实时执行句柄已失效，拒绝登记远程进程组。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var execution = await db.CommandExecutions.SingleAsync(x => x.Id == executionId, cancellationToken);
        execution.RemoteProcessId = processId;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task WriteChunkAsync(OutputStreamType streamType, string safe, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetByteCount(safe);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _totalBytes += bytes;
            if (_totalBytes > options.MaximumOutputBytes)
            {
                if (!_truncationRecorded) await MarkTruncatedAsync(cancellationToken);
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var execution = await db.CommandExecutions.SingleAsync(x => x.Id == executionId, cancellationToken);
            db.CommandOutputChunks.Add(new CommandOutputChunk { ExecutionId = executionId, Sequence = ++_sequence, StreamType = streamType, Content = safe, ByteLength = bytes });
            execution.OutputBytes = _totalBytes;
            if (streamType == OutputStreamType.StandardOutput && string.IsNullOrEmpty(execution.StandardOutputPreview)) execution.StandardOutputPreview = safe[..Math.Min(safe.Length, 2048)];
            if (streamType == OutputStreamType.StandardError && string.IsNullOrEmpty(execution.StandardErrorPreview)) execution.StandardErrorPreview = safe[..Math.Min(safe.Length, 2048)];
            await db.SaveChangesAsync(cancellationToken);
            await notifier.OutputReceivedAsync(new(executionId, _sequence, streamType, safe, bytes), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static IEnumerable<string> SplitByUtf8Bytes(string value, int maximumBytes)
    {
        if (string.IsNullOrEmpty(value)) { yield return string.Empty; yield break; }
        var start = 0;
        while (start < value.Length)
        {
            var length = Math.Min(value.Length - start, maximumBytes);
            if (start + length < value.Length && char.IsHighSurrogate(value[start + length - 1])) length--;
            while (length > 1 && Encoding.UTF8.GetByteCount(value.AsSpan(start, length)) > maximumBytes) length /= 2;
            while (start + length < value.Length && Encoding.UTF8.GetByteCount(value.AsSpan(start, length + 1)) <= maximumBytes) length++;
            yield return value.Substring(start, length);
            start += length;
        }
    }

    private async Task MarkTruncatedAsync(CancellationToken cancellationToken)
    {
        _truncationRecorded = true;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var execution = await db.CommandExecutions.SingleAsync(x => x.Id == executionId, cancellationToken);
        execution.OutputTruncated = true;
        var content = "\n[CommandHub] 输出超过配置上限，后续内容未保存。\n";
        db.CommandOutputChunks.Add(new CommandOutputChunk { ExecutionId = executionId, Sequence = ++_sequence, StreamType = OutputStreamType.System, Content = content, ByteLength = Encoding.UTF8.GetByteCount(content) });
        await db.SaveChangesAsync(cancellationToken);
        await notifier.OutputReceivedAsync(new(executionId, _sequence, OutputStreamType.System, content, Encoding.UTF8.GetByteCount(content)), cancellationToken);
    }

    public void Dispose() => _gate.Dispose();
}
