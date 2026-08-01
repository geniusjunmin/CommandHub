using System.Text;

namespace CommandHub.Domain;

public static class DomainRules
{
    public static string NormalizeCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var result = new StringBuilder(command.Length);
        var inSingle = false;
        var inDouble = false;
        var pendingSpace = false;

        foreach (var character in command.Trim())
        {
            if (character == '\'' && !inDouble) inSingle = !inSingle;
            if (character == '"' && !inSingle) inDouble = !inDouble;

            if (char.IsWhiteSpace(character) && !inSingle && !inDouble)
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }

        return result.ToString();
    }

    public static void ValidateServer(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(server.Name)) throw new DomainValidationException("服务器名称不能为空。");
        if (string.IsNullOrWhiteSpace(server.Host)) throw new DomainValidationException("主机地址不能为空。");
        if (server.Host.Contains("://", StringComparison.Ordinal)) throw new DomainValidationException("主机地址不能包含协议前缀。");
        if (server.Port is < 1 or > 65535) throw new DomainValidationException("端口必须在 1 到 65535 之间。");
        if (string.IsNullOrWhiteSpace(server.DefaultUsername)) throw new DomainValidationException("SSH 用户名不能为空。");
    }
}

public sealed class DomainValidationException(string message) : Exception(message);
