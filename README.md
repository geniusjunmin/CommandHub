# CommandHub

CommandHub 是一个开源、自托管的 Linux 服务器命令管理与非交互式远程执行平台。它把每次操作按服务器、用户、目录、时间、结果、风险和标签组织起来，让运维命令可以安全搜索、复用、审计和再次执行。

> 截图将在首个公开版本发布后补充；当前 UI 已包含概览、服务器、执行、历史、详情、模板、审计和管理页面。

## MVP 功能

- ASP.NET Core Identity 本地账户，关闭公共注册，支持锁定、TOTP、Passkey 和安全 Cookie。
- SystemAdministrator、Operator、Viewer、Auditor 角色及逐服务器资源授权。
- PostgreSQL 持久化、EF Core 迁移、Data Protection 凭据加密。
- 密码或私钥 SSH 认证；未知 Host Key 必须人工确认，后续指纹变化即拒绝连接。
- 有界后台队列；全局、服务器、用户并发限制；超时和 best-effort 取消。
- SFTP 上传临时 Bash 脚本，原始命令不进入外层 SSH 启动命令。
- stdout/stderr 独立分块保存，SignalR 授权组实时通知，输出上限和 ANSI 清理。
- 确定性风险分析、Critical 默认拒绝、高风险二次确认和敏感信息脱敏。
- 历史搜索、状态/风险筛选、收藏、修改后运行、自动分类标签。
- 参数化模板内核，严格 POSIX Shell 参数转义；Raw 参数仅管理员可保存。
- 结构化审计日志、API 资源鉴权、安全响应头、CSRF 与分区限流。

## 技术栈

.NET 10 / ASP.NET Core 10、Blazor Web App Interactive Server、EF Core 10、ASP.NET Core Identity、SignalR、PostgreSQL、SSH.NET、Bootstrap 5、xUnit、Docker Compose 和 Caddy。

主要第三方包：Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3（PostgreSQL provider）、SSH.NET 2025.1.0（SSH/SFTP）、Microsoft ASP.NET Core/EF Core 10.0.10 packages（Identity、迁移、测试和健康检查）。

## Docker 快速启动

要求 Docker Engine + Compose v2。先创建配置，并填写两个强密码：

```bash
cp .env.example .env
# 编辑 .env：POSTGRES_PASSWORD、COMMANDHUB_ADMIN_PASSWORD
docker compose config
docker compose up -d --build
docker compose ps
```

打开 `https://localhost`。Caddy 会为 localhost 生成本地 CA 证书，浏览器可能要求手工信任；生产域名会自动申请公共证书。管理员账户是 `.env` 中的 `COMMANDHUB_ADMIN_EMAIL`，密码不会写入仓库或日志。

## 本地开发

要求 .NET SDK 10.0.302+ 和 PostgreSQL 17（兼容版本亦可）。

```bash
dotnet restore
dotnet ef database update --project src/CommandHub.Infrastructure
dotnet run --project src/CommandHub.Web
dotnet test
```

使用环境变量提供管理员和连接信息：

```bash
export ConnectionStrings__DefaultConnection='Host=localhost;Database=commandhub;Username=commandhub;Password=...'
export Database__ApplyMigrationsOnStartup=true
export COMMANDHUB_ADMIN_EMAIL='admin@example.com'
export COMMANDHUB_ADMIN_PASSWORD='use-a-unique-strong-password'
```

密码至少 14 位并包含大小写字母、数字和符号。初始化是幂等的：已有管理员不会被重置，也不会在日志中打印密码。

## 添加第一台服务器

1. 以系统管理员登录，打开“服务器”并添加 Host、端口、用户、目录与凭据。
2. 点击“测试”。首次连接只采集 Host Key，不会自动信任。
3. 在服务器控制台执行 `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub -E sha256`，通过可信渠道核对算法和 SHA-256 指纹。
4. 在 CommandHub 明确确认指纹，再次测试连接。
5. 在“系统管理”中按服务器授予操作员执行权限。

## 安全警告

CommandHub 可以在远程主机执行高权限命令，应仅部署在受控网络并强制 HTTPS。不要把 PostgreSQL、Kestrel 或测试 SSH fixture 直接暴露到互联网。备份数据库时也必须同步备份 Data Protection key volume，否则已存凭据无法解密。规则风险分析不能完整理解所有 Shell 语义，不能代替最小权限、备份和人工复核。

## 当前限制与路线图

MVP 不包含 PTY/xterm.js、Vim/top 等交互应用、tmux、文件管理器、集群/Redis backplane、Vault、LDAP/OIDC/SAML、审批工作流、Agent 或 AI 命令生成。浏览器端输出详情使用 SignalR，并保留一秒轮询作为断连兼容路径。模板渲染、参数验证和 Raw 参数权限内核已实现，但完整动态参数表单、跨多台服务器的聚合执行视图、外部自动化 token/OIDC 和生产邮件发送器仍列为后续增强。

更多内容见 [架构](docs/ARCHITECTURE.md)、[安全](docs/SECURITY.md)、[部署](docs/DEPLOYMENT.md)、[用户指南](docs/USER_GUIDE.md)、[开发](docs/DEVELOPMENT.md) 和 [API](docs/API.md)。

本项目采用 [MIT License](LICENSE)。
