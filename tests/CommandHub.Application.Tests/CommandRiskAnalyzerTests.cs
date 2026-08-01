using CommandHub.Application;
using CommandHub.Domain;

namespace CommandHub.Application.Tests;

public sealed class CommandRiskAnalyzerTests
{
    private readonly CommandRiskAnalyzer _analyzer = new();

    [Theory]
    [InlineData("rm -rf /", RiskLevel.Critical)]
    [InlineData("mkfs.ext4 /dev/sda", RiskLevel.Critical)]
    [InlineData("sudo systemctl restart nginx", RiskLevel.High)]
    [InlineData("DELETE FROM users", RiskLevel.High)]
    [InlineData("apt install curl", RiskLevel.Medium)]
    [InlineData("ls -la", RiskLevel.Low)]
    public void Analyze_ReturnsConservativeLevel(string command, RiskLevel level) => Assert.Equal(level, _analyzer.Analyze(command).Level);

    [Fact]
    public void Analyze_CompoundCommand_IsAtLeastMedium() => Assert.True(_analyzer.Analyze("pwd && whoami").Level >= RiskLevel.Medium);
}
