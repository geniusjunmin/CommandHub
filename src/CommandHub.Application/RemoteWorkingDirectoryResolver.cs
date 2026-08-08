using System.Text;

namespace CommandHub.Application;

public sealed class RemoteWorkingDirectoryResolver : IRemoteWorkingDirectoryResolver
{
    public string Resolve(string? workingDirectory, string serverDefaultWorkingDirectory)
    {
        var value = string.IsNullOrWhiteSpace(workingDirectory) ? serverDefaultWorkingDirectory : workingDirectory;
        if (string.IsNullOrWhiteSpace(value)) value = "~";
        RejectUnsafe(value);

        if (value is "~" or "~/") return "\"$HOME\"";
        if (value.StartsWith("~/", StringComparison.Ordinal))
        {
            var relative = value[2..];
            RejectUnsafe(relative);
            return "\"$HOME/" + EscapeDoubleQuotedLiteral(relative) + "\"";
        }
        if (value[0] != '/')
            throw new ArgumentException("工作目录只能是 ~、~/下的路径或绝对路径。", nameof(workingDirectory));
        return TemplateRenderer.EscapePosixArgument(value);
    }

    private static void RejectUnsafe(string value)
    {
        if (value.Any(character => character == '\0' || character == '\r' || character == '\n' || char.IsControl(character)))
            throw new ArgumentException("工作目录不能包含换行、NUL 或控制字符。", nameof(value));
        if (value.Contains("$(", StringComparison.Ordinal) || value.Contains('`'))
            throw new ArgumentException("工作目录不能包含命令替换。", nameof(value));
    }

    private static string EscapeDoubleQuotedLiteral(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '"' or '$' or '`') result.Append('\\');
            result.Append(character);
        }
        return result.ToString();
    }
}
