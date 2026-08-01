const connections = new Map();

export async function connect(executionId, dotnet) {
    if (!window.signalR || connections.has(executionId)) return;
    const connection = new window.signalR.HubConnectionBuilder()
        .withUrl("/hubs/executions")
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .configureLogging(window.signalR.LogLevel.Warning)
        .build();

    connection.on("OutputReceived", output => dotnet.invokeMethodAsync("OnOutputReceived", output));
    connection.on("ExecutionStatusChanged", (_, status) => dotnet.invokeMethodAsync("OnStatusChanged", status));
    connection.on("ExecutionCompleted", (_, status, exitCode) => dotnet.invokeMethodAsync("OnExecutionCompleted", status, exitCode));
    connection.onreconnected(() => connection.invoke("JoinExecution", executionId));
    await connection.start();
    await connection.invoke("JoinExecution", executionId);
    connections.set(executionId, connection);
}

export async function disconnect(executionId) {
    const connection = connections.get(executionId);
    if (!connection) return;
    connections.delete(executionId);
    await connection.stop();
}
