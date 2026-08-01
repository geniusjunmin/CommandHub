# 安全

## 威胁模型

重点威胁包括账户撞库与 CSRF、越权查看历史、猜测执行 GUID、凭据或命令 Secret 泄漏、SSH 中间人、Shell 注入、危险命令误执行、输出内 ANSI/HTML 注入、队列耗尽和审计篡改。MVP 假设主机和 PostgreSQL 由部署者维护，CommandHub 单实例运行在受控网络。

## 身份与 Web

- 公共注册组件已移除；用户仅由管理员创建。
- Identity 登录失败计入锁定；支持 TOTP 和 Passkey；禁用用户更新 security stamp，提交新命令还会查询 `IsEnabled`。
- Cookie 使用 `__Host-` 前缀、HttpOnly、Secure、SameSite=Lax、8 小时滑动过期。
- 默认授权策略要求登录；健康检查、登录和静态资产才匿名。
- Blazor 表单使用 antiforgery；API/Hub 使用身份和资源授权；全局按身份/IP 分区限流。
- CSP、frame-ancestors、X-Frame-Options、nosniff、Referrer-Policy 和 HSTS 降低浏览器攻击面。

## 凭据与 Secret

密码、私钥和私钥密码通过 ASP.NET Core Data Protection 用独立 purpose string 加密。列表/API 永不返回密文字段；编辑只能覆盖，不能读回明文。日志只记录脱敏命令。默认不保留包含识别到 Secret 的原文；Data Protection key 与数据库必须一起备份，并限制文件权限。

## SSH Host Key

代码中不存在 `CanTrust = true`。未知密钥会中止握手并只返回算法和 SHA-256 指纹；管理员需通过独立可信渠道核对后固定。后续比较同时要求算法和指纹一致，并使用固定时间字节比较。指纹变化立即中止；重新信任要求系统管理员输入服务器名称和本人当前密码，并记录旧、新指纹及关联审计事件。

## 命令边界

原始命令 UTF-8 Base64 编码进入临时脚本；工作目录使用 POSIX 单引号转义。外层启动命令只包含固定路径和格式化 GUID，取消命令只包含验证过的正整数。模板参数默认使用 `ShellArgument`；Raw 仅系统管理员可保存。风险规则采取保守分级，Critical 默认关闭且 Production 永久拒绝。

## 输出与审计

stdout/stderr 分离、ANSI/NUL 清理、HTML 文本呈现、单块与总字节上限。加入 SignalR group、查看详情和下载输出均按执行资源鉴权。重要操作写入 append-only 应用用例的 AuditLog；数据库账户仍应在生产环境禁止普通应用角色执行 AuditLog 的 UPDATE/DELETE（可用额外 PostgreSQL 权限加固）。

## HTTPS、代理和备份

只通过 Caddy/Nginx 的 HTTPS 入口访问。可信代理必须正确传递并限制 Forwarded headers；不要公开 PostgreSQL 或 Kestrel。数据库备份和 Data Protection keys 是一个恢复单元，离线加密保存并定期演练恢复。

## 已知限制

规则分析不是 Shell parser；取消不能绝对证明远程进程已终止；单实例内存队列不提供崩溃后的 durable delivery；Data Protection 不是外部 HSM/Vault；MVP 没有双人审批、OIDC/LDAP、SIEM 输出和多节点 SignalR backplane。

## 漏洞报告

请通过仓库的私密安全报告渠道联系维护者，不要在公开 issue 中附带凭据、主机地址或可利用细节。报告应包含受影响版本、复现条件、影响和建议修复；维护者确认后协调披露时间。
