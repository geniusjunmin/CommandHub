using CommandHub.Domain;

namespace CommandHub.Domain.Tests;

public sealed class DomainRulesTests
{
    [Theory]
    [InlineData("  ls   -la  /tmp ", "ls -la /tmp")]
    [InlineData("echo 'a   b'", "echo 'a   b'")]
    [InlineData("echo \"a   b\"  &&  pwd", "echo \"a   b\" && pwd")]
    public void NormalizeCommand_PreservesQuotedWhitespace(string input, string expected) => Assert.Equal(expected, DomainRules.NormalizeCommand(input));

    [Fact]
    public void ValidateServer_RejectsProtocolPrefix()
    {
        var server = new Server { Name = "prod", Host = "ssh://host", Port = 22, DefaultUsername = "ops" };
        Assert.Throws<DomainValidationException>(() => DomainRules.ValidateServer(server));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void ValidateServer_RejectsInvalidPort(int port)
    {
        var server = new Server { Name = "prod", Host = "host", Port = port, DefaultUsername = "ops" };
        Assert.Throws<DomainValidationException>(() => DomainRules.ValidateServer(server));
    }
}
