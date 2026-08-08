using System.Data.Common;
using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure.Execution;
using CommandHub.Infrastructure.Persistence;
using CommandHub.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace CommandHub.Infrastructure.Tests;

public sealed class CancellationRaceIntegrationTests
{
    [IntegrationFact("COMMANDHUB_TEST_POSTGRES")]
    public async Task WorkerClaimWinningRace_IsObservedAsRunningAndUsesOnlyLiveLease()
    {
        var blocker = new CancellationUpdateBlocker();
        var options = new DbContextOptionsBuilder<CommandHubDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("COMMANDHUB_TEST_POSTGRES"))
            .AddInterceptors(blocker).Options;
        var factory = new TestDbFactory(options);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.MigrateAsync();
            setup.CommandExecutions.Add(CreateExecution(out var executionId, ExecutionStatus.Queued));
            await setup.SaveChangesAsync();

            var registry = new LiveExecutionRegistry();
            var handle = registry.Register(executionId, setup.CommandExecutions.Local.Single().ServerId, "fake", default);
            Assert.True(registry.TrySetRemoteProcessGroupId(executionId, handle.ExecutionNonce, 4242));
            var provider = new RecordingProvider();
            var notifier = new RecordingNotifier();
            var service = CreateService(factory, provider, registry, notifier);

            var cancellation = service.CancelAsync(executionId, "owner", false);
            await blocker.UpdateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await using var workerDb = new CommandHubDbContext(new DbContextOptionsBuilder<CommandHubDbContext>()
                .UseNpgsql(Environment.GetEnvironmentVariable("COMMANDHUB_TEST_POSTGRES")).Options);
            var claimed = await workerDb.CommandExecutions.Where(x => x.Id == executionId && x.Status == ExecutionStatus.Queued)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, ExecutionStatus.Running));
            Assert.Equal(1, claimed);
            blocker.ContinueUpdate.TrySetResult();

            var result = await cancellation;
            Assert.True(result.Requested);
            Assert.Equal(1, provider.CancelCalls);
            Assert.Equal(4242, provider.LastProcessGroupId);
            Assert.False(registry.TryAcquireCancellationLease(executionId, out _));
            registry.Remove(executionId, handle.ExecutionNonce);
        }
    }

    [IntegrationFact("COMMANDHUB_TEST_POSTGRES")]
    public async Task QueuedCancellation_IsAtomicAndPublishesBothTerminalEvents()
    {
        var options = new DbContextOptionsBuilder<CommandHubDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("COMMANDHUB_TEST_POSTGRES")).Options;
        var factory = new TestDbFactory(options);
        Guid executionId;
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.MigrateAsync();
            setup.CommandExecutions.Add(CreateExecution(out executionId, ExecutionStatus.Queued));
            await setup.SaveChangesAsync();
        }

        var notifier = new RecordingNotifier();
        var service = CreateService(factory, new RecordingProvider(), new LiveExecutionRegistry(), notifier);
        var result = await service.CancelAsync(executionId, "owner", false);
        Assert.True(result.Requested);

        await using var verify = await factory.CreateDbContextAsync();
        var execution = await verify.CommandExecutions.AsNoTracking().SingleAsync(x => x.Id == executionId);
        Assert.Equal(ExecutionStatus.Cancelled, execution.Status);
        Assert.True(execution.WasCancelled);
        Assert.NotNull(execution.CancellationRequestedAt);
        Assert.Contains((executionId, ExecutionStatus.Cancelled), notifier.Statuses);
        Assert.Contains((executionId, ExecutionStatus.Cancelled), notifier.Completed);
        var workerClaim = await verify.CommandExecutions
            .Where(x => x.Id == executionId && x.Status == ExecutionStatus.Queued && x.CancellationRequestedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, ExecutionStatus.Running));
        Assert.Equal(0, workerClaim);
    }

    private static CommandExecution CreateExecution(out Guid executionId, ExecutionStatus status)
    {
        executionId = Guid.NewGuid();
        return new CommandExecution
        {
            Id = executionId,
            Server = new Server { Name = $"race-{Guid.NewGuid():N}", Host = "localhost", DefaultUsername = "test" },
            UserId = "owner",
            CommandText = "sleep 10",
            MaskedCommandText = "sleep 10",
            NormalizedCommand = "sleep 10",
            NormalizedCommandHash = DomainRules.ComputeCommandHash("sleep 10"),
            NormalizedCommandPrefix = "sleep 10",
            Status = status,
        };
    }

    private static CommandHubService CreateService(IDbContextFactory<CommandHubDbContext> factory, ICommandExecutionProvider provider,
        ILiveExecutionRegistry registry, IExecutionNotifier notifier) => new(factory, null!, null!, null!,
            new ExecutionQueue(Options.Create(new ExecutionOptions())), provider, null!, registry, notifier,
            new RemoteWorkingDirectoryResolver(), Options.Create(new ExecutionOptions()), Options.Create(new SecurityOptions()), null!);

    private sealed class TestDbFactory(DbContextOptions<CommandHubDbContext> options) : IDbContextFactory<CommandHubDbContext>
    {
        public CommandHubDbContext CreateDbContext() => new(options);
        public Task<CommandHubDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CommandHubDbContext(options));
    }

    private sealed class CancellationUpdateBlocker : DbCommandInterceptor
    {
        public TaskCompletionSource UpdateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueUpdate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CancellationRequestedAt", StringComparison.Ordinal))
            {
                UpdateReached.TrySetResult();
                await ContinueUpdate.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class RecordingProvider : ICommandExecutionProvider
    {
        public string ProviderName => "fake";
        public int CancelCalls { get; private set; }
        public long? LastProcessGroupId { get; private set; }
        public Task<ExecutionStartResult> StartAsync(ExecutionQueueItem request, IExecutionOutputSink outputSink, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CancelExecutionResult> CancelAsync(ILiveExecutionCancellationLease lease, CancellationToken cancellationToken)
        {
            Assert.True(lease.IsValid);
            CancelCalls++;
            LastProcessGroupId = lease.RemoteProcessGroupId;
            return Task.FromResult(new CancelExecutionResult(true, true, "cancelled"));
        }
    }

    private sealed class RecordingNotifier : IExecutionNotifier
    {
        public List<(Guid, ExecutionStatus)> Statuses { get; } = [];
        public List<(Guid, ExecutionStatus)> Completed { get; } = [];
        public Task ExecutionStartedAsync(Guid executionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OutputReceivedAsync(ExecutionOutput output, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StatusChangedAsync(Guid executionId, ExecutionStatus status, CancellationToken cancellationToken) { Statuses.Add((executionId, status)); return Task.CompletedTask; }
        public Task ExecutionCompletedAsync(Guid executionId, ExecutionStatus status, int? exitCode, CancellationToken cancellationToken) { Completed.Add((executionId, status)); return Task.CompletedTask; }
    }
}
