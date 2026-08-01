# 架构

## 分层与依赖

```mermaid
flowchart LR
    Web["CommandHub.Web\nBlazor / API / SignalR / Identity host"] --> App["CommandHub.Application\n用例契约 / 风险 / 脱敏 / 模板"]
    Web --> Infra["CommandHub.Infrastructure\nEF / Identity store / SSH / Queue"]
    Infra --> App
    Infra --> Domain["CommandHub.Domain\n实体 / 枚举 / 领域规则"]
    App --> Domain
    Domain -.->|"不依赖框架"| None["ASP.NET / EF / SSH.NET 均不可见"]
```

Domain 不引用 ASP.NET Core、EF Core、SignalR 或 SSH.NET。Application 定义 `ICommandExecutionProvider`、`IExecutionOutputSink`、队列和用例接口；Infrastructure 实现 SSH provider，因此未来可并列增加 Agent、Local 或 Kubernetes provider。

## 命令执行流程

```mermaid
sequenceDiagram
    participant B as Browser
    participant W as Web/API
    participant S as CommandHubService
    participant Q as Bounded Channel
    participant H as BackgroundService
    participant SSH as SSH/SFTP Provider
    participant DB as PostgreSQL
    participant R as SignalR Hub
    B->>W: 提交命令
    W->>S: 用户、IP、User-Agent
    S->>S: 资源授权、脱敏、风险和确认
    S->>DB: Pending + AuditLog
    S->>Q: ExecutionQueueItem（原文仅内存）
    Q->>H: 消费并获取三层并发信号量
    H->>DB: Running
    H->>SSH: 上传 0700 Base64 脚本并执行
    SSH-->>H: stdout / stderr / PID 控制帧
    H->>DB: 分块、PID、状态、ExitCode
    H->>R: 授权 group 实时事件
    R-->>B: 输出与状态
    H->>DB: 自动标签、完成审计字段
```

## 状态机与取消

```mermaid
stateDiagram-v2
    [*] --> Pending
    Pending --> Queued
    Pending --> Rejected
    Queued --> Running
    Running --> Succeeded
    Running --> Failed
    Running --> TimedOut
    Running --> Cancelled
    Running --> ConnectionFailed
```

远程脚本输出经过严格解析的正整数 PID。取消使用独立 SSH 会话发送进程组 TERM，短暂宽限后发送 KILL。状态明确是 best effort，因为 SSH 断线时无法证明整个远程进程树已经结束。

## 数据模型

核心关系为 Server 1:1 ServerCredential、Server 1:N UserServerPermission、Server 1:N CommandExecution、CommandExecution 1:N CommandOutputChunk、CommandExecution N:M Tag、User N:M Execution（CommandFavorite）以及 CommandTemplate 1:N CommandTemplateParameter。审计表没有普通修改/删除用例。所有列表查询分页且只读查询使用 `AsNoTracking`。

## 安全边界

浏览器、API 输入、Shell 文本、SSH 服务器和反向代理头均不可信。Web 做身份边界，Application 做规则边界，Infrastructure 做凭据/Host Key/数据库/SSH 边界。UI 隐藏按钮仅改善体验；服务端每次执行、查看、取消、下载和加入 SignalR group 都重新授权。
