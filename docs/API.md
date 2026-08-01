# API

API 使用与 Blazor 相同的 Identity Cookie，默认要求认证并受全局 rate limiter 保护。浏览器 cookie 调用修改操作必须携带 antiforgery 上下文；面向外部自动化的独立 token/OIDC 认证尚未实现。

## 执行

`POST /api/executions`（SystemAdministrator 或 Operator）

```json
{
  "serverId": "00000000-0000-0000-0000-000000000000",
  "commandText": "df -h",
  "workingDirectory": "~",
  "timeoutSeconds": 300,
  "doNotSaveCommand": false,
  "confirmed": false,
  "confirmationServerName": null,
  "criticalConfirmationPhrase": null,
  "source": "Api"
}
```

返回 `202 Accepted` 和执行 ID；权限、Host Key、风险或队列检查失败返回 `400`，身份失败返回 `401/403`，限流返回 `429`。

- `GET /api/executions/{id}`：资源授权后的执行、输出和标签。
- `POST /api/executions/{id}/cancel`：请求 best-effort 取消。
- `GET /api/executions/{id}/output`：下载 `text/plain` 输出。

## 审计导出

`GET /api/audit.csv`（SystemAdministrator 或 Auditor）导出最近 10,000 条审计事件。响应使用 UTF-8 BOM；所有字段均按 RFC 4180 方式引用，并防护以 `= + - @` 开头的电子表格公式注入。

## SignalR

Hub：`/hubs/executions`。客户端先调用 `JoinExecution(executionId)`；服务端重新验证该用户是否能查看执行，不能通过猜测 GUID 加组。客户端事件：`ExecutionStarted`、`OutputReceived`、`ExecutionStatusChanged`、`ExecutionCompleted`。

所有错误响应不返回内部堆栈、凭据或原始敏感命令。
