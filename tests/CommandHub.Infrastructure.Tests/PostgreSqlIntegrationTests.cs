using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure.Execution;
using CommandHub.Infrastructure.Persistence;
using CommandHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CommandHub.Infrastructure.Tests;

public sealed class PostgreSqlIntegrationTests
{
    [IntegrationFact("COMMANDHUB_TEST_POSTGRES")]
    public void RuntimeModel_MatchesLatestMigrationSnapshot()
    {
        using var db = new CommandHubDbContext(new DbContextOptionsBuilder<CommandHubDbContext>()
            .UseNpgsql(RequireEnvironment("COMMANDHUB_TEST_POSTGRES")).Options);
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot ?? throw new InvalidOperationException("Missing model snapshot.");
        var runtime = db.GetService<IDesignTimeModel>().Model;
        var snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot.Model);
        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(snapshotModel.GetRelationalModel(), runtime.GetRelationalModel());
        Assert.Empty(differences);
    }

    [IntegrationFact("COMMANDHUB_TEST_POSTGRES")]
    public async Task Migrations_AndLongCommandModel_WorkOnPostgreSql()
    {
        var connectionString = RequireEnvironment("COMMANDHUB_TEST_POSTGRES");
        var options = new DbContextOptionsBuilder<CommandHubDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new CommandHubDbContext(options);
        await db.Database.MigrateAsync();

        var migrations = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(migrations, item => item.EndsWith("HardenCancellationAndLongCommands", StringComparison.Ordinal));

        var command = new string('中', DomainRules.MaximumCommandCharacters);
        var normalized = DomainRules.NormalizeCommand(command);
        var server = new Server { Name = $"integration-{Guid.NewGuid():N}", Host = "localhost", DefaultUsername = "tester" };
        var execution = new CommandExecution
        {
            Server = server,
            UserId = "integration-user",
            CommandText = command,
            MaskedCommandText = command,
            NormalizedCommand = normalized,
            NormalizedCommandHash = DomainRules.ComputeCommandHash(normalized),
            NormalizedCommandPrefix = normalized[..DomainRules.NormalizedCommandPrefixCharacters],
        };
        db.CommandExecutions.Add(execution);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.CommandExecutions.SingleAsync(item => item.Id == execution.Id);
        Assert.Equal(DomainRules.MaximumCommandCharacters, stored.CommandText!.Length);
        Assert.Equal(DomainRules.ComputeCommandHash(normalized), stored.NormalizedCommandHash);
        Assert.Equal(DomainRules.NormalizedCommandPrefixCharacters, stored.NormalizedCommandPrefix.Length);
    }

    [IntegrationFact("COMMANDHUB_TEST_POSTGRES")]
    public async Task StartupRecovery_RequeuesDurableWorkAndInterruptsOrphanedRunningExecution()
    {
        var options = new DbContextOptionsBuilder<CommandHubDbContext>().UseNpgsql(RequireEnvironment("COMMANDHUB_TEST_POSTGRES")).Options;
        var factory = new RecoveryDbFactory(options);
        var protector = new CredentialProtector(new EphemeralDataProtectionProvider());
        var server = new Server
        {
            Name = $"recovery-{Guid.NewGuid():N}",
            Host = "localhost",
            DefaultUsername = "tester",
            Credential = new ServerCredential
            {
                Username = "tester",
                CredentialType = CredentialType.Password,
                EncryptedPassword = protector.Protect("integration-test-password"),
            },
        };
        var pending = Execution(server, ExecutionStatus.Pending, "echo pending");
        var queued = Execution(server, ExecutionStatus.Queued, "echo queued");
        var running = Execution(server, ExecutionStatus.Running, "sleep 30");
        var incomplete = Execution(server, ExecutionStatus.Pending, null);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.MigrateAsync();
            await setup.CommandExecutions.Where(x => x.Status == ExecutionStatus.Pending || x.Status == ExecutionStatus.Queued || x.Status == ExecutionStatus.Running).ExecuteDeleteAsync();
            setup.AddRange(pending, queued, running, incomplete);
            await setup.SaveChangesAsync();
        }

        var queue = new ExecutionQueue(Options.Create(new ExecutionOptions { QueueCapacity = 10 }));
        var notifier = new RecoveryNotifier();
        var recovery = new ExecutionRecoveryService(factory, queue, notifier, new RemoteWorkingDirectoryResolver(), protector, Options.Create(new ExecutionOptions()), NullLogger<ExecutionRecoveryService>.Instance);
        await recovery.StartAsync(default);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        var first = reader.Current.ExecutionId;
        Assert.True(await reader.MoveNextAsync());
        var second = reader.Current.ExecutionId;
        Assert.Equal(new[] { pending.Id, queued.Id }.Order(), new[] { first, second }.Order());

        await using var verify = await factory.CreateDbContextAsync();
        var states = await verify.CommandExecutions.AsNoTracking().Where(x => new[] { pending.Id, queued.Id, running.Id, incomplete.Id }.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Status);
        Assert.Equal(ExecutionStatus.Queued, states[pending.Id]);
        Assert.Equal(ExecutionStatus.Queued, states[queued.Id]);
        Assert.Equal(ExecutionStatus.Interrupted, states[running.Id]);
        Assert.Equal(ExecutionStatus.Rejected, states[incomplete.Id]);
        Assert.Contains((running.Id, ExecutionStatus.Interrupted), notifier.Completed);
        Assert.Contains((incomplete.Id, ExecutionStatus.Rejected), notifier.Completed);
    }

    private static CommandExecution Execution(Server server, ExecutionStatus status, string? command) => new()
    {
        Server = server,
        UserId = "recovery-user",
        CommandText = command,
        MaskedCommandText = command ?? "[not-saved]",
        NormalizedCommand = command ?? "[not-saved]",
        NormalizedCommandHash = DomainRules.ComputeCommandHash(command ?? "[not-saved]"),
        NormalizedCommandPrefix = command ?? "[not-saved]",
        Status = status,
    };

    private sealed class RecoveryDbFactory(DbContextOptions<CommandHubDbContext> options) : IDbContextFactory<CommandHubDbContext>
    {
        public CommandHubDbContext CreateDbContext() => new(options);
        public Task<CommandHubDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CommandHubDbContext(options));
    }

    private sealed class RecoveryNotifier : IExecutionNotifier
    {
        public List<(Guid, ExecutionStatus)> Completed { get; } = [];
        public Task ExecutionStartedAsync(Guid executionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OutputReceivedAsync(ExecutionOutput output, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StatusChangedAsync(Guid executionId, ExecutionStatus status, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExecutionCompletedAsync(Guid executionId, ExecutionStatus status, int? exitCode, CancellationToken cancellationToken) { Completed.Add((executionId, status)); return Task.CompletedTask; }
    }

    private static string RequireEnvironment(string name) => Environment.GetEnvironmentVariable(name)!;
}
