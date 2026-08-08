using System.Text;
using CommandHub.Application;
using CommandHub.Domain;
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
        var script = SshCommandExecutionProvider.BuildScript(command, "/srv/app's data", 30, new RemoteWorkingDirectoryResolver());
        Assert.DoesNotContain(command, script, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(command)), script, StringComparison.Ordinal);
        Assert.Contains("'/srv/app'\"'\"'s data'", script, StringComparison.Ordinal);
        Assert.Contains("setsid bash", script, StringComparison.Ordinal);
        Assert.Contains("wait \"$WRAPPER_PID\"", script, StringComparison.Ordinal);
        Assert.Contains("ps -o pgid=", script, StringComparison.Ordinal);
        Assert.Contains("__COMMANDHUB_CONTROL__:PGID=", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("__COMMANDHUB_CONTROL__:PGID=", StringComparison.Ordinal) < script.IndexOf("exec timeout", StringComparison.Ordinal));
        Assert.True(script.IndexOf("__COMMANDHUB_CONTROL__:PGID=", StringComparison.Ordinal) < script.IndexOf("cd --", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveRegistry_NoncePreventsStaleHandleMutation()
    {
        var registry = new LiveExecutionRegistry();
        var id = Guid.NewGuid();
        var first = registry.Register(id, Guid.NewGuid(), "SSH", default);
        Assert.Throws<InvalidOperationException>(() => registry.Register(id, Guid.NewGuid(), "SSH", default));
        Assert.False(registry.TrySetRemoteProcessGroupId(id, first.ExecutionNonce, 0));
        Assert.False(registry.TrySetRemoteProcessGroupId(Guid.NewGuid(), first.ExecutionNonce, 101));
        Assert.True(registry.TrySetRemoteProcessGroupId(id, first.ExecutionNonce, 101));
        Assert.False(registry.Remove(id, Guid.NewGuid()));
        Assert.True(registry.Remove(id, first.ExecutionNonce));
        Assert.False(registry.Remove(id, first.ExecutionNonce));
        var second = registry.Register(id, Guid.NewGuid(), "SSH", default);
        Assert.False(registry.TrySetRemoteProcessGroupId(id, first.ExecutionNonce, 202));
        Assert.False(registry.Remove(id, first.ExecutionNonce));
        Assert.True(registry.TryAcquireCancellationLease(id, out var current));
        Assert.Equal(second.ExecutionNonce, current!.ExecutionNonce);
        Assert.Null(current.RemoteProcessGroupId);
        registry.Remove(id, second.ExecutionNonce);
        Assert.False(current.IsValid);
    }

    [Fact]
    public void LiveRegistry_OnlyCurrentExecutionCanAcquireCancellationLease()
    {
        var registry = new LiveExecutionRegistry();
        var id = Guid.NewGuid();
        var first = registry.Register(id, Guid.NewGuid(), "SSH", default);
        Assert.True(registry.TrySetRemoteProcessGroupId(id, first.ExecutionNonce, 1234));
        Assert.True(registry.TryAcquireCancellationLease(id, out var lease));
        Assert.True(lease!.IsValid);
        Assert.Equal(1234, lease.RemoteProcessGroupId);
        Assert.False(lease.CancellationTokenSource.IsCancellationRequested);
        Assert.False(registry.TryAcquireCancellationLease(id, out _));

        Assert.True(registry.Remove(id, first.ExecutionNonce));
        Assert.False(lease.IsValid);
        var replacement = registry.Register(id, Guid.NewGuid(), "SSH", default);
        Assert.False(lease.IsValid);
        Assert.False(registry.TrySetRemoteProcessGroupId(id, first.ExecutionNonce, 1234));
        Assert.True(registry.Remove(id, replacement.ExecutionNonce));
    }

    [Fact]
    public async Task LiveRegistry_RemoteSignalLeaseCoordinatesWorkerCleanup()
    {
        var registry = new LiveExecutionRegistry();
        var id = Guid.NewGuid();
        var handle = registry.Register(id, Guid.NewGuid(), "SSH", default);
        Assert.True(registry.TrySetRemoteProcessGroupId(id, handle.ExecutionNonce, 321));
        Assert.True(registry.TryAcquireCancellationLease(id, out var cancellation));
        var signal = await cancellation!.TryAcquireRemoteSignalLeaseAsync(default);
        Assert.NotNull(signal);

        var remove = Task.Run(() => registry.Remove(id, handle.ExecutionNonce));
        await Task.Delay(50);
        Assert.False(remove.IsCompleted);

        await signal!.DisposeAsync();
        Assert.True(await remove.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(cancellation.IsValid);
        Assert.False(cancellation.TryRequestLocalCancellation());
        Assert.Null(await cancellation.TryAcquireRemoteSignalLeaseAsync(default));
    }

    [Fact]
    public async Task SshPump_ParsesSplitControlFrameBeforeBusinessOutput()
    {
        var sink = new RecordingOutputSink();
        long? pgid = null;
        await SshCommandExecutionProvider.PumpAsync(
            new ChunkedReadStream(Encoding.UTF8.GetBytes("__COMMANDHUB_CONTROL__:PGID=4321\n业务😀"), 1),
            OutputStreamType.StandardError, sink, value => { pgid = value; return Task.CompletedTask; }, default);

        Assert.Equal(4321, pgid);
        Assert.Equal("业务😀", string.Concat(sink.Output.Where(x => x.Type == OutputStreamType.StandardError).Select(x => x.Content)));
    }

    [Theory]
    [InlineData("bad-frame\n")]
    [InlineData("__COMMANDHUB_CONTROL__:PGID=0\n")]
    [InlineData("__COMMANDHUB_CONTROL__:PGID=abc\n")]
    public async Task SshPump_RejectsInvalidControlFrames(string frame)
    {
        var sink = new RecordingOutputSink();
        await Assert.ThrowsAsync<InvalidDataException>(() => SshCommandExecutionProvider.PumpAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(frame)), OutputStreamType.StandardError, sink,
            _ => Task.CompletedTask, default));
    }

    [Fact]
    public async Task SshPump_RejectsIncompleteAndOversizedControlFrames()
    {
        var sink = new RecordingOutputSink();
        await Assert.ThrowsAsync<InvalidDataException>(() => SshCommandExecutionProvider.PumpAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("__COMMANDHUB_CONTROL__:PGID=12")), OutputStreamType.StandardError, sink,
            _ => Task.CompletedTask, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => SshCommandExecutionProvider.PumpAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 513))), OutputStreamType.StandardError, sink,
            _ => Task.CompletedTask, default));
    }

    [Fact]
    public async Task SshPump_ReplacesInvalidUtf8AndReportsOnlyOnce()
    {
        var sink = new RecordingOutputSink();
        await SshCommandExecutionProvider.PumpAsync(new ChunkedReadStream([0xff, 0xfe, 0x41], 1),
            OutputStreamType.StandardOutput, sink, null, default);
        Assert.Equal(1, sink.Output.Count(x => x.Type == OutputStreamType.System));
        Assert.Contains('\uFFFD', string.Concat(sink.Output.Select(x => x.Content)));
    }

    [Fact]
    public void StatefulAnsiSanitizer_RemovesSequencesAcrossChunks()
    {
        var sanitizer = new StatefulAnsiSanitizer();
        Assert.Equal("before", sanitizer.Process("before\u001b["));
        Assert.Equal("red", sanitizer.Process("31mred\u001b]0;title"));
        Assert.Equal("after", sanitizer.Process("\u001b\\after"));
    }

    private sealed class RecordingOutputSink : IExecutionOutputSink
    {
        public List<(OutputStreamType Type, string Content)> Output { get; } = [];
        public Task WriteAsync(OutputStreamType streamType, string content, CancellationToken cancellationToken)
        {
            Output.Add((streamType, content));
            return Task.CompletedTask;
        }
        public Task SetRemoteProcessGroupIdAsync(long processGroupId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ChunkedReadStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
