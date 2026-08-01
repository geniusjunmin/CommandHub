using CommandHub.Domain;

namespace CommandHub.Application;

public sealed record RiskAnalysis(RiskLevel Level, IReadOnlyList<string> Reasons);
public sealed record ExecutionQueueItem(Guid ExecutionId, Guid ServerId, string UserId, string CommandText, string WorkingDirectory, int TimeoutSeconds);
public sealed record ExecutionOutput(Guid ExecutionId, int Sequence, OutputStreamType StreamType, string Content, int ByteLength);
public sealed record ExecutionStartResult(int ExitCode, bool TimedOut, long? RemoteProcessId);
public sealed record CancelExecutionResult(bool Requested, bool RemoteTerminationConfirmed, string Message);
public sealed record LiveExecutionHandle(Guid ExecutionId, Guid ServerId, string ProviderName, long? RemoteProcessGroupId, CancellationTokenSource CancellationTokenSource, DateTimeOffset StartedAt, Guid ExecutionNonce);
public sealed record HostKeyInfo(string Algorithm, string Fingerprint);
public sealed record ConnectionTestResult(bool Success, bool RequiresHostKeyTrust, bool HostKeyChanged, HostKeyInfo HostKey, string? TrustedFingerprint, string? Hostname, string? OperatingSystem, string? Username, string? WorkingDirectory, long LatencyMilliseconds, string? Error);
public sealed record SubmissionResult(bool Accepted, Guid? ExecutionId, RiskAnalysis Risk, string? Error);

public sealed class CommandSubmission
{
    public Guid ServerId { get; set; }
    public string CommandText { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool DoNotSaveCommand { get; set; }
    public bool Confirmed { get; set; }
    public string? ConfirmationServerName { get; set; }
    public string? CriticalConfirmationPhrase { get; set; }
    public CommandSource Source { get; set; } = CommandSource.Web;
}

public sealed class ServerEditorModel
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string DefaultUsername { get; set; } = string.Empty;
    public string DefaultWorkingDirectory { get; set; } = "~";
    public ServerEnvironment Environment { get; set; }
    public bool IsEnabled { get; set; } = true;
    public CredentialType CredentialType { get; set; } = CredentialType.PrivateKey;
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
    public string? PrivateKeyPassphrase { get; set; }
}

public sealed record ServerListItem(Guid Id, string Name, string Host, int Port, string Username, ServerEnvironment Environment, bool IsEnabled, string? LastConnectionStatus, DateTimeOffset? LastConnectedAt, PermissionLevel? PermissionLevel, bool HasCredential, bool HasTrustedHostKey);
public sealed record ExecutionListItem(Guid Id, Guid ServerId, string ServerName, string MaskedCommand, string WorkingDirectory, ExecutionStatus Status, RiskLevel RiskLevel, DateTimeOffset CreatedAt, long? DurationMilliseconds, int? ExitCode, bool IsFavorite);
public sealed record ExecutionDetails(CommandExecution Execution, IReadOnlyList<CommandOutputChunk> Output, IReadOnlyList<string> Tags);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record UserListItem(string Id, string Email, string DisplayName, bool IsEnabled, IReadOnlyList<string> Roles, DateTimeOffset? LastLoginAt);

public sealed class CreateUserModel
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = SystemRoles.Viewer;
}

public sealed class PermissionEditorModel
{
    public string UserId { get; set; } = string.Empty;
    public Guid ServerId { get; set; }
    public PermissionLevel PermissionLevel { get; set; }
    public bool CanViewHistory { get; set; } = true;
    public bool CanExecuteCommands { get; set; }
    public bool CanExecuteHighRiskCommands { get; set; }
    public bool CanManageTemplates { get; set; }
}

public sealed class HistoryQuery
{
    public string? Search { get; set; }
    public Guid? ServerId { get; set; }
    public ExecutionStatus? Status { get; set; }
    public RiskLevel? RiskLevel { get; set; }
    public string? Tag { get; set; }
    public bool FavoritesOnly { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public interface ICommandRiskAnalyzer { RiskAnalysis Analyze(string command); }
public interface ICommandMaskingService { string Mask(string command); bool ContainsLikelySecret(string command); }
public interface ICommandClassificationService { IReadOnlyList<string> Classify(string command); }
public interface ITemplateRenderer { string Render(CommandTemplate commandTemplate, IReadOnlyDictionary<string, string?> values, bool allowRaw); }
public interface IRemoteWorkingDirectoryResolver { string Resolve(string? workingDirectory, string serverDefaultWorkingDirectory); }

public interface ILiveExecutionRegistry
{
    LiveExecutionHandle Register(Guid executionId, Guid serverId, string providerName, CancellationToken applicationToken);
    bool TryGet(Guid executionId, out LiveExecutionHandle? handle);
    bool TrySetRemoteProcessGroupId(Guid executionId, Guid nonce, long processGroupId);
    bool RequestCancellation(Guid executionId);
    bool Remove(Guid executionId, Guid nonce);
}

public interface IExecutionOutputSink
{
    Task WriteAsync(OutputStreamType streamType, string content, CancellationToken cancellationToken);
    Task SetRemoteProcessIdAsync(long processId, CancellationToken cancellationToken);
}

public interface IExecutionNotifier
{
    Task ExecutionStartedAsync(Guid executionId, CancellationToken cancellationToken);
    Task OutputReceivedAsync(ExecutionOutput output, CancellationToken cancellationToken);
    Task StatusChangedAsync(Guid executionId, ExecutionStatus status, CancellationToken cancellationToken);
    Task ExecutionCompletedAsync(Guid executionId, ExecutionStatus status, int? exitCode, CancellationToken cancellationToken);
}

public interface ICommandExecutionProvider
{
    string ProviderName { get; }
    Task<ExecutionStartResult> StartAsync(ExecutionQueueItem request, IExecutionOutputSink outputSink, CancellationToken cancellationToken);
    Task<CancelExecutionResult> CancelAsync(LiveExecutionHandle handle, CancellationToken cancellationToken);
}

public interface IExecutionQueue
{
    ValueTask<bool> TryEnqueueAsync(ExecutionQueueItem item, CancellationToken cancellationToken);
    IAsyncEnumerable<ExecutionQueueItem> ReadAllAsync(CancellationToken cancellationToken);
    void Complete();
}

public interface ICommandHubService
{
    Task<IReadOnlyList<ServerListItem>> GetServersAsync(string userId, bool isAdministrator, CancellationToken cancellationToken = default);
    Task<ServerEditorModel?> GetServerForEditAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> SaveServerAsync(ServerEditorModel model, string actorUserId, CancellationToken cancellationToken = default);
    Task SetServerEnabledAsync(Guid id, bool enabled, string actorUserId, CancellationToken cancellationToken = default);
    Task DeleteServerAsync(Guid id, string actorUserId, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestConnectionAsync(Guid serverId, string actorUserId, CancellationToken cancellationToken = default);
    Task TrustHostKeyAsync(Guid serverId, HostKeyInfo hostKey, string actorUserId, CancellationToken cancellationToken = default);
    Task ReTrustHostKeyAsync(Guid serverId, HostKeyInfo hostKey, string confirmationServerName, string currentPassword, string actorUserId, CancellationToken cancellationToken = default);
    Task<SubmissionResult> SubmitAsync(CommandSubmission submission, string userId, bool isAdministrator, string? clientIp, string? userAgent, CancellationToken cancellationToken = default);
    Task<CancelExecutionResult> CancelAsync(Guid executionId, string userId, bool isAdministrator, CancellationToken cancellationToken = default);
    Task<PagedResult<ExecutionListItem>> SearchHistoryAsync(HistoryQuery query, string userId, bool canAuditAll, CancellationToken cancellationToken = default);
    Task<ExecutionDetails?> GetExecutionAsync(Guid id, string userId, bool canAuditAll, CancellationToken cancellationToken = default);
    Task ToggleFavoriteAsync(Guid executionId, string userId, CancellationToken cancellationToken = default);
    Task AddTagAsync(Guid executionId, string tagName, string userId, bool canAuditAll, CancellationToken cancellationToken = default);
    Task RemoveTagAsync(Guid executionId, string tagName, string userId, bool canAuditAll, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandTemplate>> GetTemplatesAsync(string userId, CancellationToken cancellationToken = default);
    Task<Guid> SaveTemplateAsync(CommandTemplate commandTemplate, string userId, bool isAdministrator, CancellationToken cancellationToken = default);
    Task DeleteTemplateAsync(Guid templateId, string userId, bool isAdministrator, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditLog>> GetAuditLogsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
}

public interface ILoginAuditService
{
    Task RecordAsync(string email, string? userId, bool succeeded, string? clientIp, string? userAgent, CancellationToken cancellationToken = default);
}

public interface IAdministrationService
{
    Task<IReadOnlyList<UserListItem>> GetUsersAsync(CancellationToken cancellationToken = default);
    Task<string> CreateUserAsync(CreateUserModel model, string actorUserId, CancellationToken cancellationToken = default);
    Task SetUserEnabledAsync(string userId, bool enabled, string actorUserId, CancellationToken cancellationToken = default);
    Task SetPermissionAsync(PermissionEditorModel model, string actorUserId, CancellationToken cancellationToken = default);
}
