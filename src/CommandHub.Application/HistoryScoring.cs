namespace CommandHub.Application;

public static class HistoryScoring
{
    public static double Score(string command, string query, DateTimeOffset executedAt, int frequency, bool succeeded, bool currentServer, bool currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var comparison = StringComparison.OrdinalIgnoreCase;
        var score = 0d;
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var index = command.IndexOf(token, comparison);
            if (index < 0) return double.NegativeInfinity;
            score += 20;
            if (index == 0) score += 20;
            if (index > 0 && char.IsWhiteSpace(command[index - 1])) score += 8;
        }
        score += Math.Max(0, 14 - (DateTimeOffset.UtcNow - executedAt).TotalDays) * 0.5;
        score += Math.Log2(Math.Max(1, frequency)) * 3;
        if (succeeded) score += 3;
        if (currentServer) score += 5;
        if (currentDirectory) score += 4;
        return score;
    }
}
