using System.Security.Claims;
using CommandHub.Application;
using CommandHub.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CommandHub.Web.Realtime;

public interface IExecutionClient
{
    Task ExecutionStarted(Guid executionId);
    Task OutputReceived(ExecutionOutput output);
    Task ExecutionStatusChanged(Guid executionId, ExecutionStatus status);
    Task ExecutionCompleted(Guid executionId, ExecutionStatus status, int? exitCode);
}

[Authorize]
public sealed class ExecutionHub(ICommandHubService service) : Hub<IExecutionClient>
{
    public async Task JoinExecution(Guid executionId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new HubException("未登录。");
        var canAuditAll = Context.User!.IsInRole(SystemRoles.SystemAdministrator) || Context.User.IsInRole(SystemRoles.Auditor);
        if (await service.GetExecutionAsync(executionId, userId, canAuditAll, Context.ConnectionAborted) is null) throw new HubException("无权查看此执行。");
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(executionId), Context.ConnectionAborted);
    }
    internal static string GroupName(Guid executionId) => $"execution:{executionId:N}";
}

public sealed class SignalRExecutionNotifier(IHubContext<ExecutionHub, IExecutionClient> hub) : IExecutionNotifier
{
    public Task ExecutionStartedAsync(Guid executionId, CancellationToken cancellationToken) => hub.Clients.Group(ExecutionHub.GroupName(executionId)).ExecutionStarted(executionId);
    public Task OutputReceivedAsync(ExecutionOutput output, CancellationToken cancellationToken) => hub.Clients.Group(ExecutionHub.GroupName(output.ExecutionId)).OutputReceived(output);
    public Task StatusChangedAsync(Guid executionId, ExecutionStatus status, CancellationToken cancellationToken) => hub.Clients.Group(ExecutionHub.GroupName(executionId)).ExecutionStatusChanged(executionId, status);
    public Task ExecutionCompletedAsync(Guid executionId, ExecutionStatus status, int? exitCode, CancellationToken cancellationToken) => hub.Clients.Group(ExecutionHub.GroupName(executionId)).ExecutionCompleted(executionId, status, exitCode);
}
