# 开发

## 环境

- .NET SDK 10.0.302（`global.json` 固定并允许最新 patch）
- PostgreSQL 17 或兼容版本
- 可选 Docker Engine / Compose v2

```bash
dotnet restore
dotnet build
dotnet test
```

本仓库启用 Nullable、implicit usings、latest-recommended analyzers、warnings-as-errors 和 `.editorconfig`。

## 数据库迁移

```bash
dotnet tool install dotnet-ef --tool-path .tools --version 10.0.10
.tools/dotnet-ef migrations add MeaningfulName \
  --project src/CommandHub.Infrastructure \
  --output-dir Persistence/Migrations
.tools/dotnet-ef database update --project src/CommandHub.Infrastructure
```

迁移只能由 Infrastructure 拥有。提交前检查生成 SQL，不在 migration 中写生产密码或环境特定数据。

## SSH fixture

```bash
cp .env.example .env
# 设置 SSH_FIXTURE_PASSWORD
docker compose --profile test up -d ssh-fixture
ssh -p 2222 commandhub@127.0.0.1
```

fixture 仅监听 loopback，禁止在生产启用。测试连接只执行 `printf`、`uname`、`hostname`、`id -un` 和 `pwd`。

## 提交前检查

```bash
dotnet format --verify-no-changes
dotnet build --configuration Release
dotnet test --configuration Release
dotnet list package --vulnerable --include-transitive
docker compose config
docker compose build
```

新增执行 provider 必须实现 Application 的统一接口；不得让 Application 引用 SSH.NET。日志中仅允许脱敏命令，不得接受“自动信任所有 Host Key”的实现。
