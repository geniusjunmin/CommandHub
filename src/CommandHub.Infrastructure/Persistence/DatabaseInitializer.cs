using CommandHub.Domain;
using CommandHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommandHub.Infrastructure.Persistence;

public sealed class DatabaseInitializer(
    CommandHubDbContext db,
    RoleManager<IdentityRole> roleManager,
    UserManager<ApplicationUser> userManager,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(bool applyMigrations, string? adminEmail, string? adminPassword, CancellationToken cancellationToken = default)
    {
        if (applyMigrations) await db.Database.MigrateAsync(cancellationToken);
        else if (!db.Database.IsRelational()) await db.Database.EnsureCreatedAsync(cancellationToken);

        foreach (var role in SystemRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole(role));
                EnsureSuccess(roleResult, $"创建角色 {role}");
            }
        }

        if (string.IsNullOrWhiteSpace(adminEmail) && string.IsNullOrWhiteSpace(adminPassword)) return;
        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            throw new InvalidOperationException("COMMANDHUB_ADMIN_EMAIL 和 COMMANDHUB_ADMIN_PASSWORD 必须同时设置。");
        ValidateAdminPassword(adminPassword);

        var admin = await userManager.FindByEmailAsync(adminEmail);
        if (admin is null)
        {
            admin = new ApplicationUser { UserName = adminEmail, Email = adminEmail, EmailConfirmed = true, DisplayName = "系统管理员" };
            EnsureSuccess(await userManager.CreateAsync(admin, adminPassword), "创建初始管理员");
            EnsureSuccess(await userManager.AddToRoleAsync(admin, SystemRoles.SystemAdministrator), "授予系统管理员角色");
            logger.LogInformation("Initial administrator account {AdminEmail} was created.", adminEmail);
        }
    }

    public static void ValidateAdminPassword(string password)
    {
        if (password.Length < 14 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit) || !password.Any(ch => !char.IsLetterOrDigit(ch)))
            throw new InvalidOperationException("初始管理员密码至少 14 位，并同时包含大小写字母、数字和符号。");
    }

    private static void EnsureSuccess(IdentityResult result, string operation)
    {
        if (!result.Succeeded) throw new InvalidOperationException($"{operation}失败：{string.Join("; ", result.Errors.Select(error => error.Description))}");
    }
}
