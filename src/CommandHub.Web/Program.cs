using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using CommandHub.Application;
using CommandHub.Domain;
using CommandHub.Infrastructure;
using CommandHub.Infrastructure.Identity;
using CommandHub.Infrastructure.Persistence;
using CommandHub.Web.Components;
using CommandHub.Web.Components.Account;
using CommandHub.Web.Realtime;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection 未配置。");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
}).AddIdentityCookies(options =>
{
    options.ApplicationCookie!.Configure(cookie =>
    {
        cookie.Cookie.Name = "__Host-CommandHub.Auth";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
        cookie.SlidingExpiration = true;
        cookie.LoginPath = "/Account/Login";
        cookie.AccessDeniedPath = "/Account/AccessDenied";
    });
});

builder.Services.AddDbContextFactory<CommandHubDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = true;
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Password.RequiredLength = 12;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireDigit = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
}).AddRoles<IdentityRole>().AddEntityFrameworkStores<CommandHubDbContext>().AddSignInManager().AddDefaultTokenProviders();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

var keyPath = builder.Configuration["DataProtection:KeysPath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".keys");
Directory.CreateDirectory(keyPath);
builder.Services.AddDataProtection().SetApplicationName("CommandHub").PersistKeysToFileSystem(new DirectoryInfo(keyPath));
builder.Services.AddOptions<ExecutionOptions>().BindConfiguration(ExecutionOptions.SectionName).Validate(options => options.GlobalConcurrency > 0 && options.PerServerConcurrency > 0 && options.PerUserConcurrency > 0 && options.QueueCapacity > 0 && options.MaximumOutputBytes > 0, "执行配置必须为正数。").ValidateOnStart();
builder.Services.AddOptions<SecurityOptions>().BindConfiguration(SecurityOptions.SectionName).ValidateOnStart();
builder.Services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName).ValidateOnStart();
builder.Services.AddCommandHubInfrastructure();
builder.Services.AddSingleton<IExecutionNotifier, SignalRExecutionNotifier>();
builder.Services.AddSignalR(options => { options.MaximumReceiveMessageSize = 32 * 1024; options.EnableDetailedErrors = builder.Environment.IsDevelopment(); });
builder.Services.AddHealthChecks().AddDbContextCheck<CommandHubDbContext>("database", tags: ["ready"]);
builder.Services.AddProblemDetails();
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("ManageServers", policy => policy.RequireRole(SystemRoles.SystemAdministrator))
    .AddPolicy("Audit", policy => policy.RequireRole(SystemRoles.SystemAdministrator, SystemRoles.Auditor))
    .AddPolicy("Execute", policy => policy.RequireRole(SystemRoles.SystemAdministrator, SystemRoles.Operator));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var identity = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var limit = context.Request.Path.StartsWithSegments("/Account/Login") ? 5 : context.Request.Path.StartsWithSegments("/api/executions") ? 10 : 120;
        return RateLimitPartition.GetFixedWindowLimiter($"{identity}:{context.Request.Path.Value}", _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
    });
});

var app = builder.Build();
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
if (app.Environment.IsDevelopment()) app.UseMigrationsEndPoint();
else { app.UseExceptionHandler("/Error", createScopeForErrors: true); app.UseHsts(); }
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self' wss: ws:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapHub<ExecutionHub>("/hubs/executions").RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapAdditionalIdentityEndpoints();
MapCommandHubApi(app);

var databaseOptions = app.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new();
if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(databaseOptions.ApplyMigrationsOnStartup, app.Configuration["COMMANDHUB_ADMIN_EMAIL"], app.Configuration["COMMANDHUB_ADMIN_PASSWORD"]);
}
await app.RunAsync();

static void MapCommandHubApi(WebApplication app)
{
    var api = app.MapGroup("/api").RequireAuthorization();
    api.MapPost("/executions", async (CommandSubmission submission, ClaimsPrincipal user, HttpContext http, IAntiforgery antiforgery, ICommandHubService service, CancellationToken ct) =>
    {
        await antiforgery.ValidateRequestAsync(http);
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await service.SubmitAsync(submission, userId, user.IsInRole(SystemRoles.SystemAdministrator), http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString(), ct);
        return result.Accepted ? Results.Accepted($"/api/executions/{result.ExecutionId}", result) : Results.BadRequest(result);
    }).RequireAuthorization("Execute");
    api.MapGet("/executions/{id:guid}", async (Guid id, ClaimsPrincipal user, ICommandHubService service, CancellationToken ct) =>
    {
        var result = await service.GetExecutionAsync(id, user.FindFirstValue(ClaimTypes.NameIdentifier)!, user.IsInRole(SystemRoles.SystemAdministrator) || user.IsInRole(SystemRoles.Auditor), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    });
    api.MapPost("/executions/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, HttpContext http, IAntiforgery antiforgery, ICommandHubService service, CancellationToken ct) =>
    {
        await antiforgery.ValidateRequestAsync(http);
        try
        {
            return Results.Ok(await service.CancelAsync(id, user.FindFirstValue(ClaimTypes.NameIdentifier)!, user.IsInRole(SystemRoles.SystemAdministrator), ct));
        }
        catch (CommandHub.Infrastructure.Services.ExecutionCancellationConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message, status = exception.Status.ToString() });
        }
    }).RequireAuthorization("Execute");
    api.MapGet("/executions/{id:guid}/output", async (Guid id, ClaimsPrincipal user, ICommandHubService service, CancellationToken ct) =>
    {
        var result = await service.GetExecutionAsync(id, user.FindFirstValue(ClaimTypes.NameIdentifier)!, user.IsInRole(SystemRoles.SystemAdministrator) || user.IsInRole(SystemRoles.Auditor), ct);
        return result is null ? Results.NotFound() : Results.Text(string.Concat(result.Output.Select(chunk => chunk.Content)), "text/plain; charset=utf-8");
    });
    api.MapGet("/audit.csv", async (ICommandHubService service, CancellationToken ct) =>
    {
        var logs = await service.GetAuditLogsAsync(1, 10_000, ct);
        var csv = new StringBuilder("\uFEFFCreatedAt,Action,ResourceType,ResourceId,Description,UserId,CorrelationId\r\n");
        foreach (var item in logs)
        {
            csv.Append(EscapeCsv(item.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))).Append(',')
                .Append(EscapeCsv(item.Action)).Append(',')
                .Append(EscapeCsv(item.ResourceType)).Append(',')
                .Append(EscapeCsv(item.ResourceId)).Append(',')
                .Append(EscapeCsv(item.Description)).Append(',')
                .Append(EscapeCsv(item.UserId)).Append(',')
                .Append(EscapeCsv(item.CorrelationId.ToString())).Append("\r\n");
        }

        return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"commandhub-audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
    }).RequireAuthorization("Audit");
}

static string EscapeCsv(string? value)
{
    value ??= string.Empty;
    if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = $"'{value}";
    return $"\"{value.Replace("\"", "\"\"")}\"";
}

public partial class Program;
