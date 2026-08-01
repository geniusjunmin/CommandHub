using System.Text.Json;
using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure.Identity;
using CommandHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CommandHub.Infrastructure.Services;

public sealed class AdministrationService(
    UserManager<ApplicationUser> userManager,
    IDbContextFactory<CommandHubDbContext> dbFactory) : IAdministrationService
{
    public async Task<IReadOnlyList<UserListItem>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await userManager.Users.AsNoTracking().OrderBy(x => x.Email).ToListAsync(cancellationToken);
        var result = new List<UserListItem>(users.Count);
        foreach (var user in users)
            result.Add(new(user.Id, user.Email ?? string.Empty, user.DisplayName, user.IsEnabled, (await userManager.GetRolesAsync(user)).ToArray(), user.LastLoginAt));
        return result;
    }

    public async Task<string> CreateUserAsync(CreateUserModel model, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!SystemRoles.All.Contains(model.Role, StringComparer.Ordinal)) throw new ArgumentException("角色无效。");
        var user = new ApplicationUser { UserName = model.Email.Trim(), Email = model.Email.Trim(), EmailConfirmed = true, DisplayName = model.DisplayName.Trim(), IsEnabled = true };
        var result = await userManager.CreateAsync(user, model.Password);
        EnsureSuccess(result);
        EnsureSuccess(await userManager.AddToRoleAsync(user, model.Role));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditLogs.Add(new AuditLog { UserId = actorUserId, Action = "UserCreated", ResourceType = "ApplicationUser", ResourceId = user.Id, Description = $"用户 {user.Email} 已创建。", MetadataJson = JsonSerializer.Serialize(new { user.Email, model.Role }) });
        await db.SaveChangesAsync(cancellationToken);
        return user.Id;
    }

    public async Task SetUserEnabledAsync(string userId, bool enabled, string actorUserId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(userId) ?? throw new KeyNotFoundException("用户不存在。");
        user.IsEnabled = enabled;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        EnsureSuccess(await userManager.UpdateAsync(user));
        if (!enabled) await userManager.UpdateSecurityStampAsync(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditLogs.Add(new AuditLog { UserId = actorUserId, Action = enabled ? "UserEnabled" : "UserDisabled", ResourceType = "ApplicationUser", ResourceId = userId, Description = $"用户 {user.Email} 已{(enabled ? "启用" : "禁用")}。", MetadataJson = "{}" });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetPermissionAsync(PermissionEditorModel model, string actorUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _ = await db.Users.AsNoTracking().SingleAsync(x => x.Id == model.UserId, cancellationToken);
        _ = await db.Servers.AsNoTracking().SingleAsync(x => x.Id == model.ServerId, cancellationToken);
        var permission = await db.UserServerPermissions.SingleOrDefaultAsync(x => x.UserId == model.UserId && x.ServerId == model.ServerId, cancellationToken);
        if (permission is null)
        {
            permission = new UserServerPermission { UserId = model.UserId, ServerId = model.ServerId };
            db.UserServerPermissions.Add(permission);
        }
        permission.PermissionLevel = model.PermissionLevel;
        permission.CanViewHistory = model.CanViewHistory;
        permission.CanExecuteCommands = model.CanExecuteCommands;
        permission.CanExecuteHighRiskCommands = model.CanExecuteHighRiskCommands;
        permission.CanManageTemplates = model.CanManageTemplates;
        permission.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(new AuditLog { UserId = actorUserId, Action = "ServerPermissionChanged", ResourceType = "UserServerPermission", ResourceId = permission.Id.ToString(), Description = "服务器权限已更新。", MetadataJson = JsonSerializer.Serialize(new { model.UserId, model.ServerId, model.PermissionLevel, model.CanExecuteCommands, model.CanExecuteHighRiskCommands }) });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureSuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));
    }
}
