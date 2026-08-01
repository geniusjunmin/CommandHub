namespace CommandHub.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Server : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string DefaultUsername { get; set; } = string.Empty;
    public string DefaultWorkingDirectory { get; set; } = "~";
    public string? OperatingSystem { get; set; }
    public ServerEnvironment Environment { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? HostKeyAlgorithm { get; set; }
    public string? HostKeyFingerprint { get; set; }
    public string? LastConnectionStatus { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public uint RowVersion { get; set; }
    public ServerCredential? Credential { get; set; }
    public ICollection<UserServerPermission> Permissions { get; set; } = [];
}

public sealed class ServerCredential : Entity
{
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public CredentialType CredentialType { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? EncryptedPassword { get; set; }
    public string? EncryptedPrivateKey { get; set; }
    public string? EncryptedPrivateKeyPassphrase { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class UserServerPermission : Entity
{
    public string UserId { get; set; } = string.Empty;
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public PermissionLevel PermissionLevel { get; set; }
    public bool CanViewHistory { get; set; } = true;
    public bool CanExecuteCommands { get; set; }
    public bool CanExecuteHighRiskCommands { get; set; }
    public bool CanManageTemplates { get; set; }
}

public sealed class CommandExecution : Entity
{
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public string? CommandText { get; set; }
    public string NormalizedCommand { get; set; } = string.Empty;
    public string MaskedCommandText { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = "~";
    public string Shell { get; set; } = "bash";
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
    public RiskLevel RiskLevel { get; set; }
    public string RiskReasons { get; set; } = "[]";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long? DurationMilliseconds { get; set; }
    public int? ExitCode { get; set; }
    public bool WasCancelled { get; set; }
    public bool TimedOut { get; set; }
    public bool OutputTruncated { get; set; }
    public long OutputBytes { get; set; }
    public string? StandardOutputPreview { get; set; }
    public string? StandardErrorPreview { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? UserAgent { get; set; }
    public CommandSource Source { get; set; }
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public long? RemoteProcessId { get; set; }
    public ICollection<CommandOutputChunk> OutputChunks { get; set; } = [];
    public ICollection<CommandExecutionTag> ExecutionTags { get; set; } = [];
}

public sealed class CommandOutputChunk
{
    public long Id { get; set; }
    public Guid ExecutionId { get; set; }
    public CommandExecution Execution { get; set; } = null!;
    public int Sequence { get; set; }
    public OutputStreamType StreamType { get; set; }
    public string Content { get; set; } = string.Empty;
    public int ByteLength { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<CommandExecutionTag> ExecutionTags { get; set; } = [];
}

public sealed class CommandExecutionTag
{
    public Guid ExecutionId { get; set; }
    public CommandExecution Execution { get; set; } = null!;
    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
    public bool IsAutomatic { get; set; }
}

public sealed class CommandFavorite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public Guid ExecutionId { get; set; }
    public CommandExecution Execution { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CommandTemplate : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CommandText { get; set; } = string.Empty;
    public string DefaultWorkingDirectory { get; set; } = "~";
    public RiskLevel RiskLevel { get; set; }
    public bool IsShared { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public Guid? ServerId { get; set; }
    public Server? Server { get; set; }
    public uint RowVersion { get; set; }
    public ICollection<CommandTemplateParameter> Parameters { get; set; } = [];
}

public sealed class CommandTemplateParameter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public CommandTemplate Template { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TemplateParameterType DataType { get; set; }
    public TemplateEscapeMode EscapeMode { get; set; } = TemplateEscapeMode.ShellArgument;
    public bool IsRequired { get; set; }
    public string? DefaultValue { get; set; }
    public string? ValidationPattern { get; set; }
    public string? AllowedValuesJson { get; set; }
    public bool IsSensitive { get; set; }
    public int SortOrder { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public string? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public string? ClientIpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
}
