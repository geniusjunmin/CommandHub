using CommandHub.Domain;
using CommandHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CommandHub.Infrastructure.Tests;

public sealed class PostgreSqlIntegrationTests
{
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

    private static string RequireEnvironment(string name) => Environment.GetEnvironmentVariable(name)!;
}
