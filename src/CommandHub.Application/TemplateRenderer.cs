using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommandHub.Domain;

namespace CommandHub.Application;

public sealed class TemplateRenderer : ITemplateRenderer
{
    private static readonly Regex Placeholder = new(@"{{\s*(?<name>[A-Za-z][A-Za-z0-9_]*)\s*}}", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public string Render(CommandTemplate commandTemplate, IReadOnlyDictionary<string, string?> values, bool allowRaw)
    {
        ArgumentNullException.ThrowIfNull(commandTemplate);
        var parameters = commandTemplate.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);

        return Placeholder.Replace(commandTemplate.CommandText, match =>
        {
            var name = match.Groups["name"].Value;
            if (!parameters.TryGetValue(name, out var parameter)) throw new ArgumentException($"模板参数 {name} 未定义。");
            values.TryGetValue(name, out var supplied);
            var value = supplied ?? parameter.DefaultValue;
            Validate(parameter, value);
            return Escape(parameter, value ?? string.Empty, allowRaw);
        });
    }

    public static string EscapePosixArgument(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static void Validate(CommandTemplateParameter parameter, string? value)
    {
        if (parameter.IsRequired && string.IsNullOrEmpty(value)) throw new ArgumentException($"参数 {parameter.DisplayName} 必填。");
        if (value is null) return;
        if (!string.IsNullOrWhiteSpace(parameter.ValidationPattern) && !Regex.IsMatch(value, parameter.ValidationPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
            throw new ArgumentException($"参数 {parameter.DisplayName} 格式无效。");
        if (!string.IsNullOrWhiteSpace(parameter.AllowedValuesJson))
        {
            var allowed = JsonSerializer.Deserialize<string[]>(parameter.AllowedValuesJson) ?? [];
            if (!allowed.Contains(value, StringComparer.Ordinal)) throw new ArgumentException($"参数 {parameter.DisplayName} 不在允许值范围内。");
        }
        if (parameter.DataType is TemplateParameterType.Number or TemplateParameterType.Port && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException($"参数 {parameter.DisplayName} 必须是整数。");
        if (parameter.DataType == TemplateParameterType.Port && (!int.TryParse(value, out var port) || port is < 1 or > 65535))
            throw new ArgumentException($"参数 {parameter.DisplayName} 必须是合法端口。");
    }

    private static string Escape(CommandTemplateParameter parameter, string value, bool allowRaw) => parameter.EscapeMode switch
    {
        TemplateEscapeMode.Raw when allowRaw => value,
        TemplateEscapeMode.Raw => throw new UnauthorizedAccessException("只有系统管理员可使用 Raw 参数。"),
        TemplateEscapeMode.Integer => long.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        TemplateEscapeMode.BooleanFlag => bool.TryParse(value, out var enabled) && enabled ? "true" : "false",
        _ => EscapePosixArgument(value),
    };
}
