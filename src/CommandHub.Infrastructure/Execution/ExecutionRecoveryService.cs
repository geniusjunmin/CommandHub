using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure.Persistence;
using CommandHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommandHub.Infrastructure.Execution;

public sealed class ExecutionRecoveryService(
    IDbContextFactory<CommandHubDbContext> dbFactory,
    IExecutionQueue queue,
    IExecutionNotifier notifier,
    IRemoteWorkingDirectoryResolver workingDirectoryResolver,
    ICredentialProtector credentialProtector,
    IOptions<ExecutionOptions> options,
    ILogger<ExecutionRecoveryService> logger) : IHostedService
{
    private readonly ExecutionOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.RecoverOnStartup) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var interruptedIds = await db.CommandExecutions.AsNoTracking()
            .Where(x => x.Status == ExecutionStatus.Running)
            .Select(x => x.Id).ToListAsync(cancellationToken);
        if (interruptedIds.Count > 0)
        {
            await db.CommandExecutions.Where(x => interruptedIds.Contains(x.Id) && x.Status == ExecutionStatus.Running)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ExecutionStatus.Interrupted)
                    .SetProperty(x => x.FinishedAt, now)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            foreach (var id in interruptedIds)
                await notifier.ExecutionCompletedAsync(id, ExecutionStatus.Interrupted, null, cancellationToken);
        }

        var recoverable = await db.CommandExecutions.AsNoTracking()
            .Where(x => x.Status == ExecutionStatus.Pending || x.Status == ExecutionStatus.Queued)
            .Select(x => new
            {
                x.Id,
                x.ServerId,
                x.UserId,
                x.CommandText,
                x.WorkingDirectory,
                x.Server.DefaultWorkingDirectory,
                ServerEnabled = x.Server.IsEnabled,
                CredentialType = (CredentialType?)x.Server.Credential!.CredentialType,
                x.Server.Credential!.EncryptedPassword,
                x.Server.Credential!.EncryptedPrivateKey,
                x.Server.Credential!.EncryptedPrivateKeyPassphrase,
            })
            .ToListAsync(cancellationToken);
        var recoveredCount = 0;
        foreach (var execution in recoverable)
        {
            if (string.IsNullOrEmpty(execution.CommandText) || !execution.ServerEnabled
                || !HasUsableCredential(execution.CredentialType, execution.EncryptedPassword, execution.EncryptedPrivateKey, execution.EncryptedPrivateKeyPassphrase)
                || !IsValidWorkingDirectory(execution.WorkingDirectory, execution.DefaultWorkingDirectory))
            {
                await RejectAsync(db, execution.Id, now, cancellationToken);
                continue;
            }

            var queued = await db.CommandExecutions
                .Where(x => x.Id == execution.Id && (x.Status == ExecutionStatus.Pending || x.Status == ExecutionStatus.Queued))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ExecutionStatus.Queued)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (queued != 1) continue;
            var accepted = await queue.TryEnqueueAsync(new(execution.Id, execution.ServerId, execution.UserId,
                execution.CommandText, execution.WorkingDirectory, _options.DefaultTimeoutSeconds), cancellationToken);
            if (!accepted) await RejectAsync(db, execution.Id, now, cancellationToken);
            else
            {
                recoveredCount++;
                await notifier.StatusChangedAsync(execution.Id, ExecutionStatus.Queued, cancellationToken);
            }
        }
        logger.LogInformation("Recovered {QueuedCount} queued executions and interrupted {RunningCount} orphaned running executions.", recoveredCount, interruptedIds.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool IsValidWorkingDirectory(string workingDirectory, string serverDefaultWorkingDirectory)
    {
        try
        {
            _ = workingDirectoryResolver.Resolve(workingDirectory, serverDefaultWorkingDirectory);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool HasUsableCredential(CredentialType? credentialType, string? encryptedPassword, string? encryptedPrivateKey, string? encryptedPassphrase)
    {
        try
        {
            if (credentialType == CredentialType.Password)
                return !string.IsNullOrEmpty(encryptedPassword) && !string.IsNullOrEmpty(credentialProtector.Unprotect(encryptedPassword));
            if (credentialType != CredentialType.PrivateKey || string.IsNullOrEmpty(encryptedPrivateKey)
                || string.IsNullOrEmpty(credentialProtector.Unprotect(encryptedPrivateKey))) return false;
            if (!string.IsNullOrEmpty(encryptedPassphrase)) _ = credentialProtector.Unprotect(encryptedPassphrase);
            return true;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return false;
        }
    }

    private async Task RejectAsync(CommandHubDbContext db, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var updated = await db.CommandExecutions.Where(x => x.Id == id && (x.Status == ExecutionStatus.Pending || x.Status == ExecutionStatus.Queued))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ExecutionStatus.Rejected)
                .SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        if (updated != 1) return;
        await notifier.StatusChangedAsync(id, ExecutionStatus.Rejected, cancellationToken);
        await notifier.ExecutionCompletedAsync(id, ExecutionStatus.Rejected, null, cancellationToken);
    }
}
