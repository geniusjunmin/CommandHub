using CommandHub.Application;

namespace CommandHub.Application.Tests;

public sealed class RemoteWorkingDirectoryResolverTests
{
    private readonly RemoteWorkingDirectoryResolver _resolver = new();

    [Theory]
    [InlineData("~", "\"$HOME\"")]
    [InlineData("~/", "\"$HOME\"")]
    [InlineData("~/folder", "\"$HOME/folder\"")]
    [InlineData("~/folder with spaces", "\"$HOME/folder with spaces\"")]
    [InlineData("/root", "'/root'")]
    [InlineData("/srv/app", "'/srv/app'")]
    [InlineData("/srv/app's", "'/srv/app'\"'\"'s'")]
    [InlineData("/目录/应用", "'/目录/应用'")]
    [InlineData("/tmp/a;b", "'/tmp/a;b'")]
    public void Resolve_ReturnsSafeShellWord(string input, string expected) => Assert.Equal(expected, _resolver.Resolve(input, "~"));

    [Theory]
    [InlineData("/tmp/a\ncommand")]
    [InlineData("~/$(id)")]
    [InlineData("~/`id`")]
    public void Resolve_RejectsControlAndCommandSubstitution(string input) => Assert.Throws<ArgumentException>(() => _resolver.Resolve(input, "~"));

    [Fact]
    public void Resolve_UsesServerDefault() => Assert.Equal("\"$HOME/default\"", _resolver.Resolve(null, "~/default"));
}
