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
            liveRegistry.Remove(item.ExecutionId, handle.ExecutionNonce);
            var status = await WasCancellationRequestedAsync(item.ExecutionId, CancellationToken.None)
                ? ExecutionStatus.Cancelled
                : result.TimedOut ? ExecutionStatus.TimedOut : result.ExitCode == 0 ? ExecutionStatus.Succeeded : ExecutionStatus.Failed;
            await CompleteAsync(item.ExecutionId, status, result.ExitCode, result.TimedOut, status == ExecutionStatus.Cancelled, result.RemoteProcessGroupId, CancellationToken.None);
            await AddAutomaticTagsAsync(item.ExecutionId, item.CommandText, CancellationToken.None);
            await notifier.ExecutionCompletedAsync(item.ExecutionId, status, result.ExitCode, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            liveRegistry.Remove(item.ExecutionId, handle.ExecutionNonce);
            await CompleteAsync(item.ExecutionId, ExecutionStatus.Cancelled, null, false, true, null, CancellationToken.None);
            await notifier.ExecutionCompletedAsync(item.ExecutionId, ExecutionStatus.Cancelled, null, CancellationToken.None);
        }
        catch (HostKeyVerificationException exception)
        {
            liveRegistry.Remove(item.ExecutionId, handle.ExecutionNonce);
            logger.LogWarning("Execution {ExecutionId} rejected because SSH host key verification failed: {Reason}", item.ExecutionId, exception.Message);
            await CompleteAsync(item.ExecutionId, ExecutionStatus.ConnectionFailed, null, false, false, null, CancellationToken.None);
            await notifier.ExecutionCompletedAsync(item.ExecutionId, ExecutionStatus.ConnectionFailed, null, CancellationToken.None);
        }
        catch (Exception exception)
        {
            liveRegistry.Remove(item.ExecutionId, handle.ExecutionNonce);
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

    private async Task<bool> WasCancellationRequestedAsync(Guid executionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CommandExecutions.AsNoTracking().Where(x => x.Id == executionId)
            .Select(x => x.CancellationRequestedAt != null).SingleAsync(cancellationToken);
    }

    private async Task CompleteAsync(Guid executionId, ExecutionStatus status, int? exitCode, bool timedOut, bool cancelled, long? processId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var execution = await db.CommandExecutions.SingleAsync(x => x.Id == executionId, cancellationToken);
        execution.Status = status;
        execution.ExitCode = exitCode;
        execution.TimedOut = timedOut;
        execution.WasCancelled = cancelled;
        execution.RemoteProcessGroupId ??= processId;
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<OutputStreamType, StatefulAnsiSanitizer> _ansi = new();
    private int _sequence;
    private long _totalBytes;
    private bool _truncationRecorded;

    public async Task WriteAsync(OutputStreamType streamType, string content, CancellationToken cancellationToken)
    {
        var sanitizer = _ansi.GetOrAdd(streamType, static _ => new StatefulAnsiSanitizer());
        var safe = sanitizer.Process(content.Replace("\0", string.Empty, StringComparison.Ordinal));
        foreach (var chunk in SplitByUtf8Bytes(safe, options.OutputChunkBytes))
            await WriteChunkAsync(streamType, chunk, cancellationToken);
    }

    public async Task SetRemoteProcessGroupIdAsync(long processGroupId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processGroupId);
        if (!liveRegistry.TrySetRemoteProcessGroupId(executionId, executionNonce, processGroupId))
            throw new InvalidOperationException("实时执行句柄已失效，拒绝登记远程进程组。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var execution = await db.CommandExecutions.SingleAsync(x => x.Id == executionId, cancellationToken);
        execution.RemoteProcessGroupId = processGroupId;
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

internal sealed class StatefulAnsiSanitizer
{
    private State _state;
    public string Process(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (_state)
            {
                case State.Normal:
                    if (character == '\u001B') _state = State.Escape;
                    else if (character == '\u009B') _state = State.Csi;
                    else result.Append(character);
                    break;
                case State.Escape:
                    _state = character switch { '[' => State.Csi, ']' => State.Osc, _ => State.Normal };
                    break;
                case State.Csi:
                    if (character is >= '@' and <= '~') _state = State.Normal;
                    break;
                case State.Osc:
                    if (character == '\a') _state = State.Normal;
                    else if (character == '\u001B') _state = State.OscEscape;
                    break;
                case State.OscEscape:
                    _state = character == '\\' ? State.Normal : State.Osc;
                    break;
            }
        }
        return result.ToString();
    }

    private enum State { Normal, Escape, Csi, Osc, OscEscape }
}
