using CommandHub.Application;
using CommandHub.Domain;

namespace CommandHub.Application.Tests;

public sealed class TemplateRendererTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("", "''")]
    [InlineData("with space", "'with space'")]
    [InlineData("a'b", "'a'\"'\"'b'")]
    [InlineData("\"$`;\n&&||$(id)", "'\"$`;\n&&||$(id)'")]
    [InlineData("中文 Unicode", "'中文 Unicode'")]
    public void EscapePosixArgument_QuotesAsOneArgument(string input, string expected) => Assert.Equal(expected, TemplateRenderer.EscapePosixArgument(input));

    [Fact]
    public void Render_EscapesMaliciousParameter()
    {
        var template = new CommandTemplate { CommandText = "docker logs {{Name}}", Parameters = [new() { Name = "Name", DisplayName = "容器", IsRequired = true }] };
        var result = new TemplateRenderer().Render(template, new Dictionary<string, string?> { ["Name"] = "app; rm -rf /" }, false);
        Assert.Equal("docker logs 'app; rm -rf /'", result);
    }

    [Fact]
    public void Render_RejectsRawForNonAdministrator()
    {
        var template = new CommandTemplate { CommandText = "echo {{Value}}", Parameters = [new() { Name = "Value", DisplayName = "值", EscapeMode = TemplateEscapeMode.Raw }] };
        Assert.Throws<UnauthorizedAccessException>(() => new TemplateRenderer().Render(template, new Dictionary<string, string?> { ["Value"] = "x" }, false));
    }
}
