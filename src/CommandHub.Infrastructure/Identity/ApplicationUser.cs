using Microsoft.AspNetCore.Identity;

namespace CommandHub.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = "zh-CN";
    public string TimeZone { get; set; } = "UTC";
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
