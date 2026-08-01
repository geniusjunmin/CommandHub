using System.Text;
using Renci.SshNet;

namespace CommandHub.Infrastructure.Tests;

public sealed class SshFixtureIntegrationTests
{
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
}
