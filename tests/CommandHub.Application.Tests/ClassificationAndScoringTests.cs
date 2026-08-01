using CommandHub.Application;

namespace CommandHub.Application.Tests;

public sealed class ClassificationAndScoringTests
{
    [Fact]
    public void Classify_AllowsMultipleTags()
    {
        var tags = new CommandClassificationService().Classify("docker exec app curl http://localhost");
        Assert.Contains("Containers", tags);
        Assert.Contains("Networking", tags);
    }

    [Fact]
    public void Score_PrefersPrefixAndRecentSuccess()
    {
        var preferred = HistoryScoring.Score("docker ps", "docker", DateTimeOffset.UtcNow, 5, true, true, true);
        var other = HistoryScoring.Score("sudo docker ps", "docker", DateTimeOffset.UtcNow.AddDays(-30), 1, false, false, false);
        Assert.True(preferred > other);
    }
}
