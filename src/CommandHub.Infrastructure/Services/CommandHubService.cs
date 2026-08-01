using System.Text.Json;
using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure.Execution;
using CommandHub.Infrastructure.Identity;
using CommandHub.Infrastructure.Persistence;
using CommandHub.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommandHub.Infrastructure.Services;

public sealed class CommandHubService(
    IDbContextFactory<CommandHubDbContext> dbFactory,
    ICredentialProtector protector,
    ICommandRiskAnalyzer riskAnalyzer,
    ICommandMaskingService maskingService,
    IExecutionQueue queue,
    ICommandExecutionProvider executionProvider,
    SshCommandExecutionProvider sshProvider,
    ILiveExecutionRegistry liveRegistry,
    IOptions<ExecutionOptions> executionOptions,
    IOptions<SecurityOptions> securityOptions,
    UserManager<ApplicationUser> userManager) : ICommandHubService
{
    private readonly ExecutionOptions _executionOptions = executionOptions.Value;
    private readonly SecurityOptions _securityOptions = securityOptions.Value;

    public async Task<IReadOnlyList<ServerListItem>> GetServersAsync(string userId, bool isAdministrator, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Servers.AsNoTracking().Include(x => x.Credential).Include(x => x.Permissions).AsQueryable();
        if (!isAdministrator) query = query.Where(x => x.Permissions.Any(permission => permission.UserId == userId));
        return await query.OrderBy(x => x.Name).Select(x => new ServerListItem(
            x.Id, x.Name, x.Host, x.Port, x.DefaultUsername, x.Environment, x.IsEnabled,
            x.LastConnectionStatus, x.LastConnectedAt,
            x.Permissions.Where(permission => permission.UserId == userId).Select(permission => (PermissionLevel?)permission.PermissionLevel).FirstOrDefault(),
            x.Credential != null, x.HostKeyFingerprint != null)).ToListAsync(cancellationToken);
    }

    public async Task<ServerEditorModel?> GetServerForEditAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Servers.AsNoTracking().Include(x => x.Credential).Where(x => x.Id == id).Select(x => new ServerEditorModel
        {
            Id = x.Id,
            Name = x.Name,
            Description = x.Description,
            Host = x.Host,
            Port = x.Port,
            DefaultUsername = x.DefaultUsername,
            DefaultWorkingDirectory = x.DefaultWorkingDirectory,
            Environment = x.Environment,
            IsEnabled = x.IsEnabled,
            CredentialType = x.Credential == null ? CredentialType.PrivateKey : x.Credential.CredentialType,
        }).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid> SaveServerAsync(ServerEditorModel model, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Server server;
        var action = "ServerCreated";
        if (model.Id is { } id)
        {
            server = await db.Servers.Include(x => x.Credential).SingleAsync(x => x.Id == id, cancellationToken);
            action = "ServerUpdated";
        }
        else
        {
            server = new Server();
            db.Servers.Add(server);
        }

        server.Name = model.Name.Trim();
        server.Description = model.Description?.Trim();
        server.Host = model.Host.Trim();
        server.Port = model.Port;
        server.DefaultUsername = model.DefaultUsername.Trim();
        server.DefaultWorkingDirectory = string.IsNullOrWhiteSpace(model.DefaultWorkingDirectory) ? "~" : model.DefaultWorkingDirectory.Trim();
        server.Environment = model.Environment;
        server.IsEnabled = model.IsEnabled;
        server.UpdatedAt = DateTimeOffset.UtcNow;
        DomainRules.ValidateServer(server);

        var hasNewSecret = !string.IsNullOrEmpty(model.Password) || !string.IsNullOrEmpty(model.PrivateKey);
        if (server.Credential is null && !hasNewSecret) throw new DomainValidationException("新增服务器必须提供 SSH 密码或私钥。");
        if (hasNewSecret)
        {
            server.Credential ??= new ServerCredential { Server = server };
            server.Credential.CredentialType = model.CredentialType;
            server.Credential.Username = server.DefaultUsername;
            server.Credential.EncryptedPassword = string.IsNullOrEmpty(model.Password) ? null : protector.Protect(model.Password);
            server.Credential.EncryptedPrivateKey = string.IsNullOrEmpty(model.PrivateKey) ? null : protector.Protect(model.PrivateKey);
            server.Credential.EncryptedPrivateKeyPassphrase = string.IsNullOrEmpty(model.PrivateKeyPassphrase) ? null : protector.Protect(model.PrivateKeyPassphrase);
            server.Credential.UpdatedAt = DateTimeOffset.UtcNow;
            AddAudit(db, actorUserId, "CredentialUpdated", "Server", server.Id.ToString(), $"服务器 {server.Name} 的 SSH 凭据已覆盖。", new { server.Name, model.CredentialType });
        }

        if (model.Id is null)
        {
            db.UserServerPermissions.Add(new UserServerPermission
            {
                UserId = actorUserId,
                Server = server,
                PermissionLevel = PermissionLevel.Administrator,
                CanViewHistory = true,
                CanExecuteCommands = true,
                CanExecuteHighRiskCommands = true,
                CanManageTemplates = true,
            });
        }
        AddAudit(db, actorUserId, action, "Server", server.Id.ToString(), $"服务器 {server.Name} 已保存。", new { server.Name, server.Host, server.Port, server.Environment });
        await db.SaveChangesAsync(cancellationToken);
        return server.Id;
    }

    public async Task SetServerEnabledAsync(Guid id, bool enabled, string actorUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleAsync(x => x.Id == id, cancellationToken);
        server.IsEnabled = enabled;
        server.UpdatedAt = DateTimeOffset.UtcNow;
        AddAudit(db, actorUserId, enabled ? "ServerEnabled" : "ServerDisabled", "Server", id.ToString(), $"服务器 {server.Name} 已{(enabled ? "启用" : "禁用")}。", new { server.Name });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteServerAsync(Guid id, string actorUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleAsync(x => x.Id == id, cancellationToken);
        if (await db.CommandExecutions.AnyAsync(x => x.ServerId == id, cancellationToken))
        {
            server.IsEnabled = false;
            AddAudit(db, actorUserId, "ServerDisabled", "Server", id.ToString(), $"服务器 {server.Name} 有历史记录，已禁用而非删除。", new { server.Name });
        }
        else
        {
            db.Servers.Remove(server);
            AddAudit(db, actorUserId, "ServerDeleted", "Server", id.ToString(), $"服务器 {server.Name} 已删除。", new { server.Name });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(Guid serverId, string actorUserId, CancellationToken cancellationToken = default)
    {
        var result = await sshProvider.TestConnectionAsync(serverId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleAsync(x => x.Id == serverId, cancellationToken);
        server.LastConnectionStatus = result.Success ? "Connected" : result.RequiresHostKeyTrust ? "HostKeyConfirmationRequired" : "Failed";
        if (result.Success)
        {
            server.LastConnectedAt = DateTimeOffset.UtcNow;
            server.OperatingSystem = result.OperatingSystem;
        }
        AddAudit(db, actorUserId, "ConnectionTested", "Server", serverId.ToString(), result.Success ? $"服务器 {server.Name} 连接测试成功。" : $"服务器 {server.Name} 连接测试失败。", new { result.Success, result.RequiresHostKeyTrust, result.HostKey.Algorithm, result.HostKey.Fingerprint });
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task TrustHostKeyAsync(Guid serverId, HostKeyInfo hostKey, string actorUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostKey.Algorithm) || !hostKey.Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)) throw new DomainValidationException("主机密钥格式无效。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleAsync(x => x.Id == serverId, cancellationToken);
        if (server.HostKeyFingerprint is not null) throw new InvalidOperationException("已有主机密钥只能通过重新信任流程覆盖。");
        var oldFingerprint = server.HostKeyFingerprint;
        server.HostKeyAlgorithm = hostKey.Algorithm;
        server.HostKeyFingerprint = hostKey.Fingerprint;
        server.LastConnectionStatus = "HostKeyTrusted";
        AddAudit(db, actorUserId, oldFingerprint is null ? "HostKeyTrusted" : "HostKeyReTrusted", "Server", serverId.ToString(), $"服务器 {server.Name} 主机密钥已固定。", new { server.Name, OldFingerprint = oldFingerprint, NewFingerprint = hostKey.Fingerprint, hostKey.Algorithm });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReTrustHostKeyAsync(Guid serverId, HostKeyInfo hostKey, string confirmationServerName, string currentPassword, string actorUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostKey.Algorithm) || !hostKey.Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)) throw new DomainValidationException("主机密钥格式无效。");
        var actor = await userManager.FindByIdAsync(actorUserId) ?? throw new UnauthorizedAccessException("管理员账户不存在。");
        if (!await userManager.IsInRoleAsync(actor, SystemRoles.SystemAdministrator) || !await userManager.CheckPasswordAsync(actor, currentPassword))
            throw new UnauthorizedAccessException("管理员身份重新验证失败。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleAsync(x => x.Id == serverId, cancellationToken);
        if (!string.Equals(server.Name, confirmationServerName, StringComparison.Ordinal)) throw new DomainValidationException("服务器名称确认不匹配。");
        var oldFingerprint = server.HostKeyFingerprint ?? throw new InvalidOperationException("服务器没有已固定的旧主机密钥，请使用首次信任流程。");
        server.HostKeyAlgorithm = hostKey.Algorithm;
        server.HostKeyFingerprint = hostKey.Fingerprint;
        server.LastConnectionStatus = "HostKeyReTrusted";
        AddAudit(db, actorUserId, "HostKeyReTrusted", "Server", serverId.ToString(), $"服务器 {server.Name} 的主机密钥已在重新验证管理员身份后覆盖。", new { server.Name, OldFingerprint = oldFingerprint, NewFingerprint = hostKey.Fingerprint, hostKey.Algorithm });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SubmissionResult> SubmitAsync(CommandSubmission submission, string userId, bool isAdministrator, string? clientIp, string? userAgent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (string.IsNullOrWhiteSpace(submission.CommandText)) return new(false, null, new(RiskLevel.Low, []), "命令不能为空。");
        if (submission.CommandText.Length > DomainRules.MaximumCommandCharacters) return new(false, null, new(RiskLevel.High, ["命令超过长度上限"]), $"命令长度不能超过 {DomainRules.MaximumCommandCharacters} 个字符。");

        var risk = riskAnalyzer.Analyze(submission.CommandText);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == submission.ServerId, cancellationToken);
        var permission = await db.UserServerPermissions.AsNoTracking().SingleOrDefaultAsync(x => x.ServerId == submission.ServerId && x.UserId == userId, cancellationToken);
        if (user is null || !user.IsEnabled) return await RejectedAsync("用户已禁用或不存在。");
        if (server is null || !server.IsEnabled) return await RejectedAsync("服务器不存在或已禁用。");
        if (permission is null || !permission.CanExecuteCommands) return await RejectedAsync("当前用户没有此服务器的命令执行权限。");
        if (string.IsNullOrWhiteSpace(server.HostKeyFingerprint)) return await RejectedAsync("必须先确认并固定 SSH 主机密钥。");
        if (risk.Level >= RiskLevel.Medium && !submission.Confirmed) return await RejectedAsync("此命令需要额外风险确认。");
        if (risk.Level >= RiskLevel.High && !permission.CanExecuteHighRiskCommands) return await RejectedAsync("当前用户没有高风险命令执行权限。");
        if (risk.Level >= RiskLevel.High && !string.Equals(submission.ConfirmationServerName, server.Name, StringComparison.Ordinal)) return await RejectedAsync("服务器名称确认不匹配。");
        if (risk.Level == RiskLevel.Critical)
        {
            if (!isAdministrator || !_securityOptions.AllowCriticalCommands || server.Environment == ServerEnvironment.Production) return await RejectedAsync("Critical 命令被安全策略拒绝。");
            if (!string.Equals(submission.CriticalConfirmationPhrase, SecurityConfirmation.CriticalPhrase, StringComparison.Ordinal)) return await RejectedAsync("Critical 确认短语不匹配。");
        }

        var masked = maskingService.Mask(submission.CommandText);
        var normalized = DomainRules.NormalizeCommand(masked);
        var timeout = Math.Clamp(submission.TimeoutSeconds ?? _executionOptions.DefaultTimeoutSeconds, 1, _executionOptions.MaximumTimeoutSeconds);
        var execution = new CommandExecution
        {
            ServerId = server.Id,
            UserId = userId,
            CommandText = submission.DoNotSaveCommand || maskingService.ContainsLikelySecret(submission.CommandText) ? null : masked,
            MaskedCommandText = submission.DoNotSaveCommand ? "[本次命令内容未保存]" : masked,
            NormalizedCommand = submission.DoNotSaveCommand ? "[not-saved]" : normalized,
            NormalizedCommandHash = DomainRules.ComputeCommandHash(submission.DoNotSaveCommand ? "[not-saved]" : normalized),
            NormalizedCommandPrefix = (submission.DoNotSaveCommand ? "[not-saved]" : normalized)[..Math.Min(submission.DoNotSaveCommand ? 11 : normalized.Length, DomainRules.NormalizedCommandPrefixCharacters)],
            WorkingDirectory = string.IsNullOrWhiteSpace(submission.WorkingDirectory) ? server.DefaultWorkingDirectory : submission.WorkingDirectory.Trim(),
            RiskLevel = risk.Level,
            RiskReasons = JsonSerializer.Serialize(risk.Reasons),
            Source = submission.Source,
            ClientIpAddress = clientIp,
            UserAgent = userAgent,
            Status = ExecutionStatus.Pending,
        };
        db.CommandExecutions.Add(execution);
        AddAudit(db, userId, "CommandSubmitted", "CommandExecution", execution.Id.ToString(), $"已向服务器 {server.Name} 提交命令。", new { Server = server.Name, Risk = risk.Level.ToString(), Command = execution.MaskedCommandText }, clientIp, userAgent, execution.CorrelationId);
        await db.SaveChangesAsync(cancellationToken);

        var queued = await queue.TryEnqueueAsync(new(execution.Id, server.Id, userId, submission.CommandText, execution.WorkingDirectory, timeout), cancellationToken);
        execution.Status = queued ? ExecutionStatus.Queued : ExecutionStatus.Rejected;
        if (!queued) AddAudit(db, userId, "CommandRejected", "CommandExecution", execution.Id.ToString(), "执行队列已满，命令被拒绝。", new { Reason = "QueueFull" }, clientIp, userAgent, execution.CorrelationId);
        await db.SaveChangesAsync(cancellationToken);
        return new(queued, execution.Id, risk, queued ? null : "执行队列已满，请稍后重试。");

        async Task<SubmissionResult> RejectedAsync(string error)
        {
            AddAudit(db, userId, "CommandRejected", "Server", submission.ServerId.ToString(), error, new { Risk = risk.Level.ToString(), Command = maskingService.Mask(submission.CommandText) }, clientIp, userAgent);
            await db.SaveChangesAsync(cancellationToken);
            return new(false, null, risk, error);
        }
    }

    public async Task<CancelExecutionResult> CancelAsync(Guid executionId, string userId, bool isAdministrator, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var execution = await db.CommandExecutions.SingleOrDefaultAsync(x => x.Id == executionId, cancellationToken) ?? throw new KeyNotFoundException("执行记录不存在。");
        if (!isAdministrator && execution.UserId != userId) throw new UnauthorizedAccessException("无权取消此执行。");
        if (execution.Status is not (ExecutionStatus.Pending or ExecutionStatus.Queued or ExecutionStatus.Running))
            throw new ExecutionCancellationConflictException(execution.Status);
        execution.CancellationRequestedAt = DateTimeOffset.UtcNow;
        execution.CancellationRequestedByUserId = userId;
        execution.CancellationReason = "UserRequested";
        if (execution.Status is ExecutionStatus.Pending or ExecutionStatus.Queued)
        {
            execution.Status = ExecutionStatus.Cancelled;
            execution.WasCancelled = true;
            execution.FinishedAt = DateTimeOffset.UtcNow;
            AddAudit(db, userId, "CommandCancelled", "CommandExecution", executionId.ToString(), "排队中的执行已取消。", new { PreviousStatus = "Queued" }, correlationId: execution.CorrelationId);
            await db.SaveChangesAsync(cancellationToken);
            return new(true, false, "排队中的执行已取消，Worker 将跳过该任务。");
        }
        if (!liveRegistry.TryGet(executionId, out var handle) || handle is null)
        {
            AddAudit(db, userId, "CommandCancellationRequested", "CommandExecution", executionId.ToString(), "执行仍标记为 Running，但当前实例没有实时句柄；未使用历史 PID。", new { LiveHandle = false }, correlationId: execution.CorrelationId);
            await db.SaveChangesAsync(cancellationToken);
            return new(true, false, "当前实例没有实时执行句柄；为避免 PID/PGID 复用误杀，未发送远程信号。");
        }
        var localRequested = liveRegistry.RequestCancellation(executionId);
        var remote = await executionProvider.CancelAsync(handle, cancellationToken);
        AddAudit(db, userId, "CommandCancellationRequested", "CommandExecution", executionId.ToString(), remote.Message, new { LocalCancellationRequested = localRequested, remote.RemoteTerminationConfirmed });
        await db.SaveChangesAsync(cancellationToken);
        return remote with { Requested = localRequested || remote.Requested };
    }

    public async Task<PagedResult<ExecutionListItem>> SearchHistoryAsync(HistoryQuery query, string userId, bool canAuditAll, CancellationToken cancellationToken = default)
    {
        query.Page = Math.Max(1, query.Page);
        query.PageSize = Math.Clamp(query.PageSize, 1, 100);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = db.CommandExecutions.AsNoTracking().AsQueryable();
        if (!canAuditAll) source = source.Where(x => x.UserId == userId || x.Server.Permissions.Any(p => p.UserId == userId && p.CanViewHistory));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            foreach (var token in query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var tokenPattern = $"%{token}%";
                source = source.Where(x => EF.Functions.ILike(x.MaskedCommandText, tokenPattern) || EF.Functions.ILike(x.Server.Name, tokenPattern) || EF.Functions.ILike(x.WorkingDirectory, tokenPattern) || (x.StandardOutputPreview != null && EF.Functions.ILike(x.StandardOutputPreview, tokenPattern)));
            }
        }
        if (query.ServerId is { } serverId) source = source.Where(x => x.ServerId == serverId);
        if (query.Status is { } status) source = source.Where(x => x.Status == status);
        if (query.RiskLevel is { } risk) source = source.Where(x => x.RiskLevel == risk);
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var normalizedTag = query.Tag.ToUpperInvariant();
            source = source.Where(x => x.ExecutionTags.Any(tag => tag.Tag.NormalizedName == normalizedTag));
        }
        if (query.FavoritesOnly) source = source.Where(x => db.CommandFavorites.Any(favorite => favorite.ExecutionId == x.Id && favorite.UserId == userId));
        if (query.From is { } from) source = source.Where(x => x.CreatedAt >= from);
        if (query.To is { } to) source = source.Where(x => x.CreatedAt <= to);
        var total = await source.CountAsync(cancellationToken);
        var projected = source.Select(x => new ExecutionListItem(x.Id, x.ServerId, x.Server.Name, x.MaskedCommandText, x.WorkingDirectory, x.Status, x.RiskLevel, x.CreatedAt, x.DurationMilliseconds, x.ExitCode, db.CommandFavorites.Any(f => f.ExecutionId == x.Id && f.UserId == userId)));
        IReadOnlyList<ExecutionListItem> items;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var candidates = await projected.OrderByDescending(x => x.CreatedAt).Take(1000).ToListAsync(cancellationToken);
            items = candidates.OrderByDescending(x => HistoryScoring.Score(x.MaskedCommand, query.Search, x.CreatedAt, 1, x.Status == ExecutionStatus.Succeeded, false, false))
                .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();
        }
        else
        {
            items = await projected.OrderByDescending(x => x.CreatedAt).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
        }
        return new(items, total, query.Page, query.PageSize);
    }

    public async Task<ExecutionDetails?> GetExecutionAsync(Guid id, string userId, bool canAuditAll, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allowed = canAuditAll || await db.CommandExecutions.AnyAsync(x => x.Id == id && (x.UserId == userId || x.Server.Permissions.Any(p => p.UserId == userId && p.CanViewHistory)), cancellationToken);
        if (!allowed) return null;
        var execution = await db.CommandExecutions.AsNoTracking().Include(x => x.Server).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (execution is null) return null;
        var output = await db.CommandOutputChunks.AsNoTracking().Where(x => x.ExecutionId == id).OrderBy(x => x.Sequence).Take(10000).ToListAsync(cancellationToken);
        var tags = await db.CommandExecutionTags.AsNoTracking().Where(x => x.ExecutionId == id).OrderBy(x => x.Tag.Name).Select(x => x.Tag.Name).ToListAsync(cancellationToken);
        return new(execution, output, tags);
    }

    public async Task ToggleFavoriteAsync(Guid executionId, string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.CommandExecutions.AnyAsync(x => x.Id == executionId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("只能收藏自己的执行记录。");
        var favorite = await db.CommandFavorites.SingleOrDefaultAsync(x => x.ExecutionId == executionId && x.UserId == userId, cancellationToken);
        if (favorite is null) db.CommandFavorites.Add(new CommandFavorite { ExecutionId = executionId, UserId = userId }); else db.CommandFavorites.Remove(favorite);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddTagAsync(Guid executionId, string tagName, string userId, bool canAuditAll, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTagName(tagName);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await EnsureCanViewExecutionAsync(db, executionId, userId, canAuditAll, cancellationToken);
        var tag = await db.Tags.SingleOrDefaultAsync(x => x.NormalizedName == normalized, cancellationToken);
        if (tag is null) { tag = new Tag { Name = tagName.Trim(), NormalizedName = normalized }; db.Tags.Add(tag); }
        if (!await db.CommandExecutionTags.AnyAsync(x => x.ExecutionId == executionId && x.TagId == tag.Id, cancellationToken))
            db.CommandExecutionTags.Add(new CommandExecutionTag { ExecutionId = executionId, Tag = tag, IsAutomatic = false });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveTagAsync(Guid executionId, string tagName, string userId, bool canAuditAll, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTagName(tagName);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await EnsureCanViewExecutionAsync(db, executionId, userId, canAuditAll, cancellationToken);
        var link = await db.CommandExecutionTags.SingleOrDefaultAsync(x => x.ExecutionId == executionId && x.Tag.NormalizedName == normalized, cancellationToken);
        if (link is not null) { db.CommandExecutionTags.Remove(link); await db.SaveChangesAsync(cancellationToken); }
    }

    public async Task<IReadOnlyList<CommandTemplate>> GetTemplatesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CommandTemplates.AsNoTracking().Include(x => x.Parameters).Where(x => x.IsShared || x.CreatedByUserId == userId).OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<Guid> SaveTemplateAsync(CommandTemplate commandTemplate, string userId, bool isAdministrator, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandTemplate.Name) || string.IsNullOrWhiteSpace(commandTemplate.CommandText)) throw new DomainValidationException("模板名称和命令不能为空。");
        DomainRules.ValidateCommandLength(commandTemplate.CommandText);
        ValidateTemplateParameters(commandTemplate);
        if (!isAdministrator && commandTemplate.Parameters.Any(x => x.EscapeMode == TemplateEscapeMode.Raw)) throw new UnauthorizedAccessException("只有系统管理员可以保存 Raw 参数。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CommandTemplates.Include(x => x.Parameters).SingleOrDefaultAsync(x => x.Id == commandTemplate.Id, cancellationToken);
        if (existing is null)
        {
            commandTemplate.CreatedByUserId = userId;
            commandTemplate.UpdatedAt = DateTimeOffset.UtcNow;
            db.CommandTemplates.Add(commandTemplate);
        }
        else
        {
            if (!isAdministrator && existing.CreatedByUserId != userId) throw new UnauthorizedAccessException("无权修改此模板。");
            existing.Name = commandTemplate.Name.Trim();
            existing.Description = commandTemplate.Description?.Trim();
            existing.CommandText = commandTemplate.CommandText;
            existing.DefaultWorkingDirectory = commandTemplate.DefaultWorkingDirectory;
            existing.RiskLevel = commandTemplate.RiskLevel;
            existing.IsShared = commandTemplate.IsShared;
            existing.ServerId = commandTemplate.ServerId;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            var incoming = commandTemplate.Parameters.ToDictionary(x => x.Id);
            foreach (var removed in existing.Parameters.Where(x => !incoming.ContainsKey(x.Id)).ToList()) db.CommandTemplateParameters.Remove(removed);
            foreach (var parameter in commandTemplate.Parameters)
            {
                var target = existing.Parameters.SingleOrDefault(x => x.Id == parameter.Id);
                if (target is null) { target = new CommandTemplateParameter { Id = parameter.Id, Template = existing }; existing.Parameters.Add(target); }
                target.Name = parameter.Name.Trim(); target.DisplayName = parameter.DisplayName.Trim(); target.Description = parameter.Description?.Trim();
                target.DataType = parameter.DataType; target.EscapeMode = parameter.EscapeMode; target.IsRequired = parameter.IsRequired;
                target.DefaultValue = parameter.IsSensitive ? null : parameter.DefaultValue; target.ValidationPattern = parameter.ValidationPattern;
                target.AllowedValuesJson = parameter.AllowedValuesJson; target.IsSensitive = parameter.IsSensitive; target.SortOrder = parameter.SortOrder;
            }
            commandTemplate = existing;
        }
        AddAudit(db, userId, "TemplateSaved", "CommandTemplate", commandTemplate.Id.ToString(), $"命令模板 {commandTemplate.Name} 已保存。", new { commandTemplate.Name, commandTemplate.IsShared });
        await db.SaveChangesAsync(cancellationToken);
        return commandTemplate.Id;
    }

    public async Task DeleteTemplateAsync(Guid templateId, string userId, bool isAdministrator, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var commandTemplate = await db.CommandTemplates.SingleAsync(x => x.Id == templateId, cancellationToken);
        if (!isAdministrator && commandTemplate.CreatedByUserId != userId) throw new UnauthorizedAccessException("无权删除此模板。");
        db.CommandTemplates.Remove(commandTemplate);
        AddAudit(db, userId, "TemplateDeleted", "CommandTemplate", templateId.ToString(), $"命令模板 {commandTemplate.Name} 已删除。", new { commandTemplate.Name });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLog>> GetAuditLogsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AuditLogs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Skip((Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 200)).Take(Math.Clamp(pageSize, 1, 200)).ToListAsync(cancellationToken);
    }

    private static void AddAudit(CommandHubDbContext db, string? userId, string action, string resourceType, string? resourceId, string description, object metadata, string? ip = null, string? userAgent = null, Guid? correlationId = null)
    {
        db.AuditLogs.Add(new AuditLog { UserId = userId, Action = action, ResourceType = resourceType, ResourceId = resourceId, Description = description, MetadataJson = JsonSerializer.Serialize(metadata), ClientIpAddress = ip, UserAgent = userAgent, CorrelationId = correlationId ?? Guid.NewGuid() });
    }

    private static string NormalizeTagName(string tagName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        var trimmed = tagName.Trim();
        if (trimmed.Length > 80) throw new ArgumentException("标签名称不能超过 80 个字符。", nameof(tagName));
        return trimmed.ToUpperInvariant();
    }

    private static void ValidateTemplateParameters(CommandTemplate commandTemplate)
    {
        var duplicate = commandTemplate.Parameters.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new DomainValidationException($"模板参数 {duplicate.Key} 重复。");
        var placeholders = System.Text.RegularExpressions.Regex.Matches(commandTemplate.CommandText, @"{{\s*(?<name>[A-Za-z][A-Za-z0-9_]*)\s*}}", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)).Select(x => x.Groups["name"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = commandTemplate.Parameters.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var undefined = placeholders.Except(definitions).FirstOrDefault();
        if (undefined is not null) throw new DomainValidationException($"占位符 {undefined} 没有参数定义。");
        var unused = definitions.Except(placeholders).FirstOrDefault();
        if (unused is not null) throw new DomainValidationException($"参数 {unused} 未在命令中使用。");
        foreach (var parameter in commandTemplate.Parameters)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(parameter.Name, @"^[A-Za-z][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw new DomainValidationException("参数名称格式无效。");
            if (parameter.IsSensitive) parameter.DefaultValue = null;
            if (!string.IsNullOrWhiteSpace(parameter.ValidationPattern)) _ = new System.Text.RegularExpressions.Regex(parameter.ValidationPattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
    }

    private static async Task EnsureCanViewExecutionAsync(CommandHubDbContext db, Guid executionId, string userId, bool canAuditAll, CancellationToken cancellationToken)
    {
        if (!canAuditAll && !await db.CommandExecutions.AnyAsync(x => x.Id == executionId && (x.UserId == userId || x.Server.Permissions.Any(p => p.UserId == userId && p.CanViewHistory)), cancellationToken))
            throw new UnauthorizedAccessException("无权修改此执行的标签。");
    }
}

public sealed class ExecutionCancellationConflictException(ExecutionStatus status)
    : InvalidOperationException($"状态为 {status} 的执行不能取消。")
{
    public ExecutionStatus Status { get; } = status;
}
