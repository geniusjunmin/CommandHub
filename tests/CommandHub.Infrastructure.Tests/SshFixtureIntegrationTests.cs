using System.Text;
using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure.Execution;
using CommandHub.Infrastructure.Persistence;
using CommandHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace CommandHub.Infrastructure.Tests;

public sealed class SshFixtureIntegrationTests
{
    [IntegrationFact("COMMANDHUB_TEST_SSH_PASSWORD")]
    public async Task CommandHubProvider_ExecutesStderrFirstStreamsAndCleansScript()
    {
        var connectionString = Environment.GetEnvironmentVariable("COMMANDHUB_TEST_POSTGRES")
            ?? throw new InvalidOperationException("COMMANDHUB_TEST_POSTGRES is required with the SSH fixture.");
        var password = Environment.GetEnvironmentVariable("COMMANDHUB_TEST_SSH_PASSWORD")!;
        string? algorithm = null;
        string? fingerprint = null;
        using (var discovery = new SshClient("127.0.0.1", 2222, "commandhub", password))
        {
            discovery.HostKeyReceived += (_, args) =>
            {
                algorithm = args.HostKeyName;
                fingerprint = args.FingerPrintSHA256.StartsWith("SHA256:", StringComparison.Ordinal)
                    ? args.FingerPrintSHA256 : $"SHA256:{args.FingerPrintSHA256.TrimEnd('=')}";
                args.CanTrust = true;
            };
            discovery.Connect();
        }

        var dbOptions = new DbContextOptionsBuilder<CommandHubDbContext>().UseNpgsql(connectionString).Options;
        var factory = new ProviderDbFactory(dbOptions);
        var protector = new CredentialProtector(new EphemeralDataProtectionProvider());
        var serverId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.MigrateAsync();
            var server = new Server
            {
                Id = serverId,
                Name = $"provider-{Guid.NewGuid():N}",
                Host = "127.0.0.1",
                Port = 2222,
                DefaultUsername = "commandhub",
                DefaultWorkingDirectory = "~",
                HostKeyAlgorithm = algorithm,
                HostKeyFingerprint = fingerprint,
                Credential = new ServerCredential { Username = "commandhub", CredentialType = CredentialType.Password, EncryptedPassword = protector.Protect(password) },
            };
            setup.CommandExecutions.Add(new CommandExecution
            {
                Id = executionId,
                Server = server,
                UserId = "provider-test",
                CommandText = "test",
                MaskedCommandText = "test",
                NormalizedCommand = "test",
                NormalizedCommandHash = DomainRules.ComputeCommandHash("test"),
                NormalizedCommandPrefix = "test",
                Status = ExecutionStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var executionOptions = new ExecutionOptions { MaximumOutputBytes = 64 * 1024, OutputChunkBytes = 4096, ConnectionTimeoutSeconds = 10 };
        var provider = new SshCommandExecutionProvider(factory, protector, new RemoteWorkingDirectoryResolver(), Options.Create(executionOptions), NullLogger<SshCommandExecutionProvider>.Instance);
        await using (var clearTrust = await factory.CreateDbContextAsync())
        {
            var server = await clearTrust.Servers.SingleAsync(x => x.Id == serverId);
            server.HostKeyAlgorithm = null;
            server.HostKeyFingerprint = null;
            await clearTrust.SaveChangesAsync();
        }
        var firstContact = await provider.TestConnectionAsync(serverId, default);
        Assert.False(firstContact.Success);
        Assert.True(firstContact.RequiresHostKeyTrust);
        await using (var trust = await factory.CreateDbContextAsync())
        {
            var server = await trust.Servers.SingleAsync(x => x.Id == serverId);
            server.HostKeyAlgorithm = algorithm;
            server.HostKeyFingerprint = fingerprint;
            await trust.SaveChangesAsync();
        }
        Assert.True((await provider.TestConnectionAsync(serverId, default)).Success);
        await using (var changeTrust = await factory.CreateDbContextAsync())
        {
            var server = await changeTrust.Servers.SingleAsync(x => x.Id == serverId);
            server.HostKeyFingerprint = fingerprint + "-changed";
            await changeTrust.SaveChangesAsync();
        }
        var changedHostKey = await provider.TestConnectionAsync(serverId, default);
        Assert.False(changedHostKey.Success);
        Assert.True(changedHostKey.HostKeyChanged);
        await using (var restoreTrust = await factory.CreateDbContextAsync())
        {
            var server = await restoreTrust.Servers.SingleAsync(x => x.Id == serverId);
            server.HostKeyFingerprint = fingerprint;
            await restoreTrust.SaveChangesAsync();
        }

        var registry = new LiveExecutionRegistry();
        var handle = registry.Register(executionId, serverId, "SSH", default);
        var notifier = new ProviderNotifier();
        using var sink = new DatabaseOutputSink(executionId, handle.ExecutionNonce, factory, notifier, registry, executionOptions);
        var request = new ExecutionQueueItem(executionId, serverId, "provider-test",
            "pwd; printf 'stderr-first\\377' >&2; printf '__COMMANDHUB_CONTROL__:PGID=999\\n' >&2; head -c 10485760 /dev/zero | tr '\\0' x >&2; printf 'no-newline中文😀'; printf '\\033['; printf '31mred\\033[0m'", "~", 20);

        var result = await provider.StartAsync(request, sink, default);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.RemoteProcessGroupId > 0);
        Assert.True(registry.TryAcquireCancellationLease(executionId, out var lease));
        Assert.Equal(result.RemoteProcessGroupId, lease!.RemoteProcessGroupId);
        registry.Remove(executionId, handle.ExecutionNonce);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.CommandExecutions.AsNoTracking().SingleAsync(x => x.Id == executionId);
        var output = await verify.CommandOutputChunks.AsNoTracking().Where(x => x.ExecutionId == executionId).OrderBy(x => x.Sequence).ToListAsync();
        Assert.True(stored.OutputTruncated);
        Assert.Single(output, x => x.StreamType == OutputStreamType.System && x.Content.Contains("非法 UTF-8", StringComparison.Ordinal));
        Assert.Single(output, x => x.StreamType == OutputStreamType.System && x.Content.Contains("输出超过", StringComparison.Ordinal));
        Assert.DoesNotContain(output, x => x.Content.Contains('\u001b'));
        Assert.Contains(output, x => x.StreamType == OutputStreamType.StandardOutput && x.Content.Contains("/config", StringComparison.Ordinal));
        Assert.Contains(output, x => x.StreamType == OutputStreamType.StandardError && x.Content.Contains("__COMMANDHUB_CONTROL__:PGID=999", StringComparison.Ordinal));

        var missingDirectoryExecutionId = Guid.NewGuid();
        await using (var addMissingDirectory = await factory.CreateDbContextAsync())
        {
            addMissingDirectory.CommandExecutions.Add(new CommandExecution
            {
                Id = missingDirectoryExecutionId,
                ServerId = serverId,
                UserId = "provider-test",
                CommandText = "pwd",
                MaskedCommandText = "pwd",
                NormalizedCommand = "pwd",
                NormalizedCommandHash = DomainRules.ComputeCommandHash("pwd"),
                NormalizedCommandPrefix = "pwd",
                Status = ExecutionStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
            });
            await addMissingDirectory.SaveChangesAsync();
        }
        var missingDirectoryHandle = registry.Register(missingDirectoryExecutionId, serverId, "SSH", default);
        using (var missingDirectorySink = new DatabaseOutputSink(missingDirectoryExecutionId, missingDirectoryHandle.ExecutionNonce, factory, notifier, registry, executionOptions))
        {
            var missingDirectory = await provider.StartAsync(
                new(missingDirectoryExecutionId, serverId, "provider-test", "pwd", $"~/missing-{Guid.NewGuid():N}", 20),
                missingDirectorySink, default);
            Assert.Equal(125, missingDirectory.ExitCode);
            Assert.True(missingDirectory.RemoteProcessGroupId > 0);
        }
        registry.Remove(missingDirectoryExecutionId, missingDirectoryHandle.ExecutionNonce);
        await using (var verifyMissingDirectory = await factory.CreateDbContextAsync())
        {
            Assert.Contains(await verifyMissingDirectory.CommandOutputChunks.Where(x => x.ExecutionId == missingDirectoryExecutionId).ToListAsync(),
                x => x.StreamType == OutputStreamType.StandardError && x.Content.Contains("Unable to enter working directory", StringComparison.Ordinal));
        }

        var cancelledExecutionId = Guid.NewGuid();
        await using (var addCancellation = await factory.CreateDbContextAsync())
        {
            addCancellation.CommandExecutions.Add(new CommandExecution
            {
                Id = cancelledExecutionId,
                ServerId = serverId,
                UserId = "provider-test",
                CommandText = "trap TERM",
                MaskedCommandText = "trap TERM",
                NormalizedCommand = "trap term",
                NormalizedCommandHash = DomainRules.ComputeCommandHash("trap term"),
                NormalizedCommandPrefix = "trap term",
                Status = ExecutionStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
            });
            await addCancellation.SaveChangesAsync();
        }
        var cancellationHandle = registry.Register(cancelledExecutionId, serverId, "SSH", default);
        using var cancellationSink = new DatabaseOutputSink(cancelledExecutionId, cancellationHandle.ExecutionNonce, factory, notifier, registry, executionOptions);
        var running = provider.StartAsync(new(cancelledExecutionId, serverId, "provider-test", "trap '' TERM; while :; do sleep 1; done", "~", 60), cancellationSink, cancellationHandle.CancellationTokenSource.Token);
        long? cancellationPgid = null;
        for (var attempt = 0; attempt < 100 && cancellationPgid is null; attempt++)
        {
            await Task.Delay(50);
            await using var poll = await factory.CreateDbContextAsync();
            cancellationPgid = await poll.CommandExecutions.Where(x => x.Id == cancelledExecutionId).Select(x => x.RemoteProcessGroupId).SingleAsync();
        }
        Assert.True(cancellationPgid > 0);
        Assert.True(registry.TryAcquireCancellationLease(cancelledExecutionId, out var cancellationLease));
        var cancelled = await provider.CancelAsync(cancellationLease!, default);
        Assert.True(cancelled.Requested);
        Assert.True(cancelled.RemoteTerminationConfirmed);
        cancellationLease!.CancellationTokenSource.Cancel();
        _ = await running;
        registry.Remove(cancelledExecutionId, cancellationHandle.ExecutionNonce);

        using var cleanup = new SshClient("127.0.0.1", 2222, "commandhub", password);
        cleanup.HostKeyReceived += (_, args) => args.CanTrust = true;
        cleanup.Connect();
        using var probe = cleanup.CreateCommand($"test ! -e /tmp/commandhub/{executionId:N}.sh");
        probe.Execute();
        Assert.Equal(0, probe.ExitStatus);
        using var cancelledProbe = cleanup.CreateCommand($"kill -0 -- -{cancellationPgid} 2>/dev/null");
        cancelledProbe.Execute();
        Assert.NotEqual(0, cancelledProbe.ExitStatus);
    }

    [IntegrationFact("COMMANDHUB_TEST_SSH_PASSWORD")]
    public async Task PasswordAuthentication_HostKeyAndStreamingScenarios_Work()
    {
        var password = Environment.GetEnvironmentVariable("COMMANDHUB_TEST_SSH_PASSWORD")
            ?? throw new InvalidOperationException("Integration test environment was removed after discovery.");
        byte[]? trustedHostKey = null;

        using (var discovery = new SshClient("127.0.0.1", 2222, "commandhub", password))
        {
            discovery.HostKeyReceived += (_, args) => { trustedHostKey = args.HostKey; args.CanTrust = false; };
            Assert.ThrowsAny<Exception>(() => discovery.Connect());
        }
        Assert.NotNull(trustedHostKey);

        using var client = new SshClient("127.0.0.1", 2222, "commandhub", password);
        client.HostKeyReceived += (_, args) => args.CanTrust = args.HostKey.AsSpan().SequenceEqual(trustedHostKey);
        client.Connect();
        Assert.True(client.IsConnected);

        using var command = client.CreateCommand("cd -- \"$HOME\" && pwd; printf no-newline; printf '中文😀'; printf stderr-ok >&2; exit 7");
        var asyncResult = command.BeginExecute();
        using var stdout = new StreamReader(command.OutputStream, new UTF8Encoding(false, true));
        using var stderr = new StreamReader(command.ExtendedOutputStream, new UTF8Encoding(false, true));
        var stdoutTask = stdout.ReadToEndAsync();
        var stderrTask = stderr.ReadToEndAsync();
        command.EndExecute(asyncResult);
        var standardOutput = await stdoutTask;
        var standardError = await stderrTask;

        Assert.Equal(7, command.ExitStatus);
        Assert.Contains("/config", standardOutput, StringComparison.Ordinal);
        Assert.EndsWith("no-newline中文😀", standardOutput, StringComparison.Ordinal);
        Assert.Equal("stderr-ok", standardError);
    }

    private sealed class ProviderDbFactory(DbContextOptions<CommandHubDbContext> options) : IDbContextFactory<CommandHubDbContext>
    {
        public CommandHubDbContext CreateDbContext() => new(options);
        public Task<CommandHubDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CommandHubDbContext(options));
    }

    private sealed class ProviderNotifier : IExecutionNotifier
    {
        public Task ExecutionStartedAsync(Guid executionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OutputReceivedAsync(ExecutionOutput output, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StatusChangedAsync(Guid executionId, ExecutionStatus status, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExecutionCompletedAsync(Guid executionId, ExecutionStatus status, int? exitCode, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
