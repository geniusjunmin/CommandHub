using System.Text.Json;
using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CommandHub.Infrastructure.Services;

public sealed class LoginAuditService(IDbContextFactory<CommandHubDbContext> dbFactory) : ILoginAuditService
{
    public async Task RecordAsync(string email, string? userId, bool succeeded, string? clientIp, string? userAgent, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (succeeded && userId is not null)
        {
            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
            if (user is not null) { user.LastLoginAt = DateTimeOffset.UtcNow; user.UpdatedAt = DateTimeOffset.UtcNow; }
        }
        db.AuditLogs.Add(new AuditLog { UserId = userId, Action = succeeded ? "LoginSucceeded" : "LoginFailed", ResourceType = "ApplicationUser", ResourceId = userId, Description = succeeded ? "用户登录成功。" : "用户登录失败。", MetadataJson = JsonSerializer.Serialize(new { Email = email.Trim().ToUpperInvariant() }), ClientIpAddress = clientIp, UserAgent = userAgent });
        await db.SaveChangesAsync(cancellationToken);
    }
}
