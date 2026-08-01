using CommandHub.Application;

namespace CommandHub.Application.Tests;

public sealed class CommandMaskingServiceTests
{
    private readonly CommandMaskingService _service = new();

    [Theory]
    [InlineData("curl -H 'Authorization: Bearer abc123' https://host", "abc123")]
    [InlineData("export OPENAI_API_KEY=secret-value", "secret-value")]
    [InlineData("mysql -uroot -pMyPassword", "MyPassword")]
    [InlineData("curl -u user:password https://host", "password")]
    public void Mask_RemovesSecret(string command, string secret)
    {
        var masked = _service.Mask(command);
        Assert.DoesNotContain(secret, masked, StringComparison.Ordinal);
        Assert.Contains("********", masked, StringComparison.Ordinal);
    }
}
