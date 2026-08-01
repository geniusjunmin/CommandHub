using CommandHub.Application;
using CommandHub.Infrastructure.Execution;
using CommandHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CommandHub.Infrastructure.Tests;

public sealed class SecurityAndExecutionTests
{
    [Fact]
    public void CredentialProtector_RoundTripsWithoutPlaintext()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new CredentialProtector(provider);
        const string secret = "Never-Store-Me-In-Plaintext";
        var encrypted = protector.Protect(secret);
        Assert.DoesNotContain(secret, encrypted, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(encrypted));
    }

    [Fact]
    public async Task ExecutionQueue_RejectsWhenFull()
    {
        var queue = new ExecutionQueue(Options.Create(new ExecutionOptions { QueueCapacity = 1 }));
        var first = new ExecutionQueueItem(Guid.NewGuid(), Guid.NewGuid(), "user", "pwd", "~", 30);
        var second = first with { ExecutionId = Guid.NewGuid() };
        Assert.True(await queue.TryEnqueueAsync(first, default));
        Assert.False(await queue.TryEnqueueAsync(second, default));
    }

    [Fact]
    public void BuildScript_DoesNotPlaceRawCommandInOuterInvocation()
    {
        const string command = "echo hello; rm -rf /tmp/example";
        var script = SshCommandExecutionProvider.BuildScript(command, "/srv/app's data", 30);
        Assert.DoesNotContain(command, script, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(command)), script, StringComparison.Ordinal);
        Assert.Contains("'/srv/app'\"'\"'s data'", script, StringComparison.Ordinal);
        Assert.Contains("setsid timeout", script, StringComparison.Ordinal);
        Assert.Contains("ps -o pgid=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PID=$$", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveRegistry_NoncePreventsStaleHandleMutation()
    {
        var registry = new LiveExecutionRegistry();
        var id = Guid.NewGuid();
        var first = registry.Register(id, Guid.NewGuid(), "SSH", default);
        Assert.True(registry.TrySetRemoteProcessGroupId(id, first.ExecutionNonce, 101));
        Assert.True(registry.Remove(id, first.ExecutionNonce));
        var second = registry.Register(id, Guid.NewGuid(), "SSH", default);
        Assert.False(registry.TrySetRemoteProcessGroupId(id, first.ExecutionNonce, 202));
        Assert.False(registry.Remove(id, first.ExecutionNonce));
        Assert.True(registry.TryGet(id, out var current));
        Assert.Equal(second.ExecutionNonce, current!.ExecutionNonce);
        Assert.Null(current.RemoteProcessGroupId);
        registry.Remove(id, second.ExecutionNonce);
    }
}
