using CommandHub.Application;

namespace CommandHub.Infrastructure.Execution;

public sealed class HostKeyVerificationException(HostKeyInfo hostKey, bool changed)
    : Exception(changed ? "SSH 主机密钥与已固定指纹不一致。" : "SSH 主机密钥尚未信任。")
{
    public HostKeyInfo HostKey { get; } = hostKey;
    public bool Changed { get; } = changed;
}
