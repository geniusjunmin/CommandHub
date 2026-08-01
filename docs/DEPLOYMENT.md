# 部署

## Docker Compose

```bash
cp .env.example .env
chmod 600 .env
# 填写强随机 POSTGRES_PASSWORD 和 COMMANDHUB_ADMIN_PASSWORD
docker compose config
docker compose up -d --build
docker compose ps
docker compose logs --tail=100 commandhub
```

服务仅通过 Caddy 的 80/443 暴露；PostgreSQL 和 Kestrel 只在内部 bridge 网络。生产设置真实 `COMMANDHUB_HOST` 并确保 DNS 指向主机，Caddy 将自动申请并续期证书。localhost 使用 Caddy 本地 CA。

## PostgreSQL 与迁移

容器启动时 `Database__ApplyMigrationsOnStartup=true`。严格变更流程可将其设为 false，在维护窗口使用同版本镜像单独运行 migration bundle，完成后再升级应用。不要让多个实例同时自动迁移。

## Nginx 示例

```nginx
server {
    listen 443 ssl http2;
    server_name commandhub.example.com;
    ssl_certificate /etc/letsencrypt/live/commandhub/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/commandhub/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 3600;
    }
}
```

若不用 Compose，Kestrel 应只监听 loopback 或私网。将受信代理/网段加入 Forwarded Headers 配置；不要无条件信任公网来源的 `X-Forwarded-*`。

## 备份与恢复

```bash
docker compose exec -T postgres pg_dump -U commandhub -d commandhub -Fc > commandhub.dump
docker run --rm -v commandhub_data-protection-keys:/source:ro -v "$PWD":/backup alpine tar czf /backup/commandhub-keys.tgz -C /source .
```

恢复到停机的新环境：

```bash
docker compose stop commandhub
docker compose exec -T postgres dropdb -U commandhub --if-exists commandhub
docker compose exec -T postgres createdb -U commandhub commandhub
docker compose exec -T postgres pg_restore -U commandhub -d commandhub --clean --if-exists < commandhub.dump
# 将 keys 归档恢复到 data-protection-keys volume，再启动应用
docker compose up -d
```

没有原 Data Protection keys 时，历史与审计仍可读取，但服务器凭据无法解密，必须重新录入。

## 升级与回滚

1. 备份数据库和 keys，并记录当前镜像 digest。
2. 在 staging 对备份副本执行迁移与 smoke tests。
3. 拉取/构建新镜像，滚动前保持单实例。
4. 检查 `/health/live`、`/health/ready`、登录和只读测试连接。
5. 应用回滚可恢复旧镜像；如果迁移不可向后兼容，必须恢复升级前数据库与 keys，不要盲目执行 `database update` 到旧版本。
# Explicit database migration

Production Compose does not apply schema migrations automatically. Before starting a new Web image, back up PostgreSQL and run the migration as an explicit deployment step:

```bash
dotnet ef database update --project src/CommandHub.Infrastructure --startup-project src/CommandHub.Infrastructure
```

The `HardenCancellationAndLongCommands` migration converts full command fields to PostgreSQL `text`, removes the unsafe full-command B-tree index, adds the indexed SHA-256 normalized-command hash and prefix, backfills existing rows, and adds durable cancellation-request metadata. It requires permission to install the PostgreSQL `pgcrypto` extension.
