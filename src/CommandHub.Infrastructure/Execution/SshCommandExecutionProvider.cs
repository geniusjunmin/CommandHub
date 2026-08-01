using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure.Persistence;
using CommandHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CommandHub.Infrastructure.Execution;

public sealed class SshCommandExecutionProvider(
    IDbContextFactory<CommandHubDbContext> dbFactory,
    ICredentialProtector protector,
    IOptions<ExecutionOptions> options,
    ILogger<SshCommandExecutionProvider> logger) : ICommandExecutionProvider
{
    private static readonly Regex ControlPid = new(@"^__COMMANDHUB_CONTROL__:PID=(?<pid>[1-9][0-9]*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly ExecutionOptions _options = options.Value;
    public string ProviderName => "SSH";

    public async Task<ExecutionStartResult> StartAsync(ExecutionQueueItem request, IExecutionOutputSink outputSink, CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(request.ServerId, cancellationToken);
        var remotePath = $"/tmp/commandhub/{request.ExecutionId:N}.sh";
        var script = BuildScript(request.CommandText, request.WorkingDirectory, request.TimeoutSeconds);

        using var sftp = CreateSftpClient(target);
        using var ssh = CreateSshClient(target);
        try
        {
            await Task.Run(sftp.Connect, cancellationToken);
            using (var mkdir = CreateSshClient(target))
            {
                await Task.Run(mkdir.Connect, cancellationToken);
                using var command = mkdir.CreateCommand("mkdir -p /tmp/commandhub && chmod 700 /tmp/commandhub");
                await command.ExecuteAsync(cancellationToken);
            }
            await using (var content = new MemoryStream(Encoding.UTF8.GetBytes(script), writable: false))
            {
                await Task.Run(() => sftp.UploadFile(content, remotePath, true), cancellationToken);
            }
            sftp.ChangePermissions(remotePath, 448);

            await Task.Run(ssh.Connect, cancellationToken);
            using var remoteCommand = ssh.CreateCommand($"bash /tmp/commandhub/{request.ExecutionId:N}.sh");
            remoteCommand.CommandTimeout = TimeSpan.FromSeconds(Math.Min(request.TimeoutSeconds + 15, _options.MaximumTimeoutSeconds + 15));
            var asyncResult = remoteCommand.BeginExecute();

            long? processId = null;
            var stdoutTask = PumpAsync(remoteCommand.OutputStream, OutputStreamType.StandardOutput, outputSink, null, cancellationToken);
            var stderrTask = PumpAsync(remoteCommand.ExtendedOutputStream, OutputStreamType.StandardError, outputSink, async pid => { processId = pid; await outputSink.SetRemoteProcessIdAsync(pid, cancellationToken); }, cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
            remoteCommand.EndExecute(asyncResult);
            return new(remoteCommand.ExitStatus ?? -1, remoteCommand.ExitStatus == 124, processId);
        }
        finally
        {
            try
            {
                if (sftp.IsConnected) sftp.DeleteFile(remotePath);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Unable to remove remote execution script {ExecutionId}.", request.ExecutionId);
            }
        }
    }

    public async Task<CancelExecutionResult> CancelAsync(LiveExecutionHandle handle, CancellationToken cancellationToken)
    {
        if (handle.RemoteProcessGroupId is not > 0) return new(true, false, "尚未取得实时远程进程组 ID；已请求取消本地执行，远程终止状态无法确认。");
        var target = await LoadTargetAsync(handle.ServerId, cancellationToken);
        using var ssh = CreateSshClient(target);
        await Task.Run(ssh.Connect, cancellationToken);
        var pid = handle.RemoteProcessGroupId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using (var terminate = ssh.CreateCommand($"kill -TERM -- -{pid}")) await terminate.ExecuteAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        using var probe = ssh.CreateCommand($"kill -0 -- -{pid} 2>/dev/null");
        await probe.ExecuteAsync(cancellationToken);
        if (probe.ExitStatus == 0)
        {
            using var kill = ssh.CreateCommand($"kill -KILL -- -{pid} 2>/dev/null || true");
            await kill.ExecuteAsync(cancellationToken);
        }
        return new(true, probe.ExitStatus != 0, probe.ExitStatus != 0 ? "远程进程组已结束。" : "已发送 TERM/KILL 信号；请在服务器侧复核。");
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        SshTarget? target = null;
        try { target = await LoadTargetAsync(serverId, cancellationToken); }
        catch (Exception exception) { return Failed(exception.Message); }

        try
        {
            using var ssh = CreateSshClient(target!);
            await Task.Run(ssh.Connect, cancellationToken);
            using var command = ssh.CreateCommand("printf 'COMMANDHUB_OK\\n'; uname -s; hostname; id -un; pwd");
            await command.ExecuteAsync(cancellationToken);
            using var outputReader = new StreamReader(command.OutputStream, Encoding.UTF8, true, 1024, leaveOpen: true);
            var result = await outputReader.ReadToEndAsync(cancellationToken);
            var lines = result.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (command.ExitStatus != 0 || lines.Length < 5 || lines[0] != "COMMANDHUB_OK") return Failed("安全探测命令返回了无效结果。");
            return new(true, false, false, new(target!.Algorithm ?? string.Empty, target.Fingerprint ?? string.Empty), target.Fingerprint, lines[2], lines[1], lines[3], lines[4], stopwatch.ElapsedMilliseconds, null);
        }
        catch (HostKeyVerificationException exception)
        {
            return new(false, !exception.Changed, exception.Changed, exception.HostKey, target?.Fingerprint, null, null, null, null, stopwatch.ElapsedMilliseconds, exception.Message);
        }
        catch (Exception exception) when (exception is SshException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return Failed("SSH 连接失败。请检查地址、端口、凭据和网络。");
        }

        ConnectionTestResult Failed(string error) => new(false, false, false, new(target?.Algorithm ?? string.Empty, target?.Fingerprint ?? string.Empty), target?.Fingerprint, null, null, null, null, stopwatch.ElapsedMilliseconds, error);
    }

    private async Task<SshTarget> LoadTargetAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.AsNoTracking().Include(item => item.Credential).SingleOrDefaultAsync(item => item.Id == serverId, cancellationToken)
            ?? throw new InvalidOperationException("服务器不存在。");
        if (!server.IsEnabled) throw new InvalidOperationException("服务器已禁用。");
        var credential = server.Credential ?? throw new InvalidOperationException("服务器没有可用凭据。");
        return new(server.Host, server.Port, credential.Username,
            credential.EncryptedPassword is null ? null : protector.Unprotect(credential.EncryptedPassword),
            credential.EncryptedPrivateKey is null ? null : protector.Unprotect(credential.EncryptedPrivateKey),
            credential.EncryptedPrivateKeyPassphrase is null ? null : protector.Unprotect(credential.EncryptedPrivateKeyPassphrase),
            server.HostKeyAlgorithm, server.HostKeyFingerprint);
    }

    private SshClient CreateSshClient(SshTarget target)
    {
        var client = new SshClient(CreateConnectionInfo(target));
        client.HostKeyReceived += (_, eventArgs) => VerifyHostKey(target, eventArgs);
        return client;
    }

    private SftpClient CreateSftpClient(SshTarget target)
    {
        var client = new SftpClient(CreateConnectionInfo(target));
        client.HostKeyReceived += (_, eventArgs) => VerifyHostKey(target, eventArgs);
        return client;
    }

    private ConnectionInfo CreateConnectionInfo(SshTarget target)
    {
        AuthenticationMethod authentication = target.PrivateKey is not null
            ? new PrivateKeyAuthenticationMethod(target.Username, new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(target.PrivateKey)), target.PrivateKeyPassphrase))
            : new PasswordAuthenticationMethod(target.Username, target.Password ?? throw new InvalidOperationException("密码凭据为空。"));
        return new ConnectionInfo(target.Host, target.Port, target.Username, authentication) { Timeout = TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds) };
    }

    private static void VerifyHostKey(SshTarget target, HostKeyEventArgs eventArgs)
    {
        var fingerprint = eventArgs.FingerPrintSHA256;
        var info = new HostKeyInfo(eventArgs.HostKeyName, fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ? fingerprint : $"SHA256:{fingerprint.TrimEnd('=')}");
        if (string.IsNullOrWhiteSpace(target.Fingerprint)) throw new HostKeyVerificationException(info, false);
        var expected = Encoding.UTF8.GetBytes(target.Fingerprint);
        var actual = Encoding.UTF8.GetBytes(info.Fingerprint);
        var matches = expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual)
            && string.Equals(target.Algorithm, info.Algorithm, StringComparison.Ordinal);
        eventArgs.CanTrust = matches;
        if (!matches) throw new HostKeyVerificationException(info, true);
    }

    private static async Task PumpAsync(Stream stream, OutputStreamType type, IExecutionOutputSink sink, Func<long, Task>? processId, CancellationToken cancellationToken)
    {
        const int bufferSize = 8192;
        var bytes = new byte[bufferSize];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bufferSize)];
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var controlPending = processId is not null;
        var controlBuffer = new StringBuilder(128);
        while (true)
        {
            var count = await stream.ReadAsync(bytes, cancellationToken);
            var flush = count == 0;
            decoder.Convert(bytes.AsSpan(0, count), characters, flush, out _, out var charsUsed, out _);
            if (charsUsed > 0)
            {
                var text = new string(characters, 0, charsUsed);
                if (controlPending)
                {
                    controlBuffer.Append(text);
                    if (controlBuffer.Length > 512) throw new InvalidDataException("SSH 控制帧超过长度上限。");
                    var newline = controlBuffer.ToString().IndexOf('\n', StringComparison.Ordinal);
                    if (newline < 0)
                    {
                        if (flush) throw new InvalidDataException("SSH 控制帧不完整。");
                        continue;
                    }
                    var frame = controlBuffer.ToString(0, newline).TrimEnd('\r');
                    var control = ControlPid.Match(frame);
                    if (!control.Success || !long.TryParse(control.Groups["pid"].Value, out var parsed) || parsed <= 0)
                        throw new InvalidDataException("SSH 控制帧无效。");
                    await processId!(parsed);
                    text = controlBuffer.ToString(newline + 1, controlBuffer.Length - newline - 1);
                    controlPending = false;
                    controlBuffer.Clear();
                }
                if (text.Length > 0) await sink.WriteAsync(type, text, cancellationToken);
            }
            if (flush) break;
        }
    }

    internal static string BuildScript(string command, string workingDirectory, int timeoutSeconds)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        var directory = new RemoteWorkingDirectoryResolver().Resolve(workingDirectory, "~");
        return $"""
#!/usr/bin/env bash
set +e
cd -- {directory}
if [ "$?" -ne 0 ]; then
  echo "Unable to enter working directory." >&2
  exit 125
fi
COMMAND_TEXT="$(printf '%s' '{encoded}' | base64 -d)"
setsid timeout --signal=TERM --kill-after=5s {timeoutSeconds} bash -lc "$COMMAND_TEXT" &
COMMAND_PID=$!
COMMAND_PGID="$(ps -o pgid= -p "$COMMAND_PID" | tr -d '[:space:]')"
case "$COMMAND_PGID" in (''|*[!0-9]*) echo "Unable to determine remote process group." >&2; kill "$COMMAND_PID" 2>/dev/null; exit 126;; esac
echo "__COMMANDHUB_CONTROL__:PID=$COMMAND_PGID" >&2
wait "$COMMAND_PID"
exit $?
""";
    }

    private sealed record SshTarget(string Host, int Port, string Username, string? Password, string? PrivateKey, string? PrivateKeyPassphrase, string? Algorithm, string? Fingerprint);
}
