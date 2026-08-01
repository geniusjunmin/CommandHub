using System.Text.RegularExpressions;
using CommandHub.Domain;

namespace CommandHub.Application;

public sealed class CommandRiskAnalyzer : ICommandRiskAnalyzer
{
    private static readonly (Regex Pattern, RiskLevel Level, string Reason)[] Rules =
    [
        (Rx(@"\brm\s+-[^\r\n]*r[^\r\n]*f[^\r\n]*\s+(/|/\*)\s*$"), RiskLevel.Critical, "递归删除根文件系统"),
        (Rx(@"\b(mkfs|wipefs|fdisk|parted|shutdown|poweroff|reboot)\b|\binit\s+[06]\b|\bdd\s+if=|\b(drop\s+(database|schema)|truncate\s+|userdel|deluser|iptables\s+-F|nft\s+flush\s+ruleset|docker\s+system\s+prune\s+-a|podman\s+system\s+prune\s+-a)"), RiskLevel.Critical, "检测到可能造成不可逆系统或数据损坏的操作"),
        (Rx(@"\bchmod\s+-R\s+777\s+/|\bchown\s+-R\s+[^\r\n]+\s+/"), RiskLevel.Critical, "递归修改根目录权限或所有者"),
        (Rx(@"\brm\s+-[^\r\n]*r[^\r\n]*f|\bsudo\b|\bsystemctl\s+(stop|disable|restart)\b|\bkill\s+-9\b|\bpkill\b|\bdocker\s+(rm|rmi)\b|\bdocker\s+compose\s+down\b|\b(apt|dnf|yum)\s+(remove|purge)\b|\b(drop|alter)\s+table\b"), RiskLevel.High, "检测到高风险管理或删除操作"),
        (Rx(@"\bupdate\s+\S+(?![\s\S]*\bwhere\b)|\bdelete\s+from\s+\S+(?![\s\S]*\bwhere\b)"), RiskLevel.High, "数据库修改未检测到 WHERE 条件"),
        (Rx(@"\b(apt|dnf|yum)\s+install\b|\bsystemctl\s+(start|reload)\b|\b(chmod|chown|mv)\b|\bdocker\s+(restart|exec)\b|\bkubectl\s+(apply|delete)\b"), RiskLevel.Medium, "检测到会改变系统状态的操作"),
    ];

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public RiskAnalysis Analyze(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var normalized = DomainRules.NormalizeCommand(command);
        var reasons = new List<string>();
        var level = RiskLevel.Low;

        foreach (var rule in Rules)
        {
            if (!rule.Pattern.IsMatch(normalized)) continue;
            if (rule.Level > level) level = rule.Level;
            reasons.Add(rule.Reason);
        }

        if (Regex.IsMatch(normalized, @"\$\(|`|\|\||&&|;|\r|\n|(^|\s)[<>](>|<)?", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
        {
            if (level < RiskLevel.Medium) level = RiskLevel.Medium;
            reasons.Add("命令包含复合执行、替换或重定向语法");
        }

        if (reasons.Count == 0) reasons.Add("未命中已知写入或破坏性规则；规则分析不能完整理解 Shell 语义");
        return new(level, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }
}

public sealed class CommandMaskingService : ICommandMaskingService
{
    private static readonly Regex[] Patterns =
    [
        Rx(@"(?i)(authorization\s*:\s*bearer\s+)([^\s""']+)"),
        Rx(@"(?i)((?:api[_-]?key|token|secret|password|passwd|aws_access_key_id|aws_secret_access_key|github_token|openai_api_key)\s*=\s*)([^\s;]+)"),
        Rx(@"(?i)((?:--password)(?:\s+|=))([^\s]+)"),
        Rx(@"(?i)(\bmysql\b[^\r\n]*?(?:\s-p))([^\s]+)"),
        Rx(@"(?i)(\bcurl\b[^\r\n]*?\s-u\s+[^:\s]+:)([^\s]+)"),
        Rx(@"(?i)(postgres(?:ql)?://[^:\s]+:)([^@\s]+)(@)"),
    ];

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public string Mask(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var masked = command;
        foreach (var pattern in Patterns)
        {
            masked = pattern.Replace(masked, match => match.Groups.Count > 3
                ? $"{match.Groups[1].Value}********{match.Groups[3].Value}"
                : $"{match.Groups[1].Value}********");
        }
        return masked;
    }

    public bool ContainsLikelySecret(string command) => !string.Equals(command, Mask(command), StringComparison.Ordinal);
}

public sealed class CommandClassificationService : ICommandClassificationService
{
    private static readonly (string Tag, string[] Commands)[] Rules =
    [
        ("Containers", ["docker", "podman"]), ("Kubernetes", ["kubectl", "helm"]), ("Git", ["git"]),
        ("Debian Packages", ["apt", "dpkg"]), ("RPM Packages", ["dnf", "yum", "rpm"]),
        ("System Services", ["systemctl", "journalctl"]), ("Web Server", ["nginx", "apachectl"]),
        ("Database", ["mysql", "mariadb", "psql", "redis-cli"]),
        ("Networking", ["ip", "ss", "ping", "traceroute", "curl", "wget"]),
        ("Permissions", ["chmod", "chown", "useradd", "usermod"]), ("Storage", ["df", "du", "lsblk", "mount"]),
        ("Monitoring", ["ps", "top", "free", "uptime"]), ("Files and Backup", ["tar", "zip", "unzip", "rsync"]),
        ("Certificates", ["openssl", "certbot"]), ("Firewall", ["ufw", "iptables", "nft"]),
    ];

    public IReadOnlyList<string> Classify(string command)
    {
        var tokens = Regex.Matches(command, @"[\p{L}\p{N}_.-]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))
            .Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Rules.Where(rule => rule.Commands.Any(tokens.Contains)).Select(rule => rule.Tag).Distinct().ToArray();
    }
}
