using CommandHub.Application;
using CommandHub.Infrastructure.Execution;
using CommandHub.Infrastructure.Persistence;
using CommandHub.Infrastructure.Security;
using CommandHub.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CommandHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCommandHubInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ICommandRiskAnalyzer, CommandRiskAnalyzer>();
        services.AddSingleton<ICommandMaskingService, CommandMaskingService>();
        services.AddSingleton<ICommandClassificationService, CommandClassificationService>();
        services.AddSingleton<ITemplateRenderer, TemplateRenderer>();
        services.AddSingleton<IRemoteWorkingDirectoryResolver, RemoteWorkingDirectoryResolver>();
        services.AddSingleton<ICredentialProtector, CredentialProtector>();
        services.AddSingleton<IExecutionQueue, ExecutionQueue>();
        services.AddSingleton<ILiveExecutionRegistry, LiveExecutionRegistry>();
        services.AddSingleton<SshCommandExecutionProvider>();
        services.AddSingleton<ICommandExecutionProvider>(provider => provider.GetRequiredService<SshCommandExecutionProvider>());
        services.AddScoped<ICommandHubService, CommandHubService>();
        services.AddScoped<IAdministrationService, AdministrationService>();
        services.AddScoped<ILoginAuditService, LoginAuditService>();
        services.AddScoped<DatabaseInitializer>();
        services.AddHostedService<ExecutionRecoveryService>();
        services.AddHostedService<ExecutionWorkerService>();
        return services;
    }
}
