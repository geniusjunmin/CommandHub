namespace CommandHub.Infrastructure.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(string requiredEnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(requiredEnvironmentVariable)))
            Skip = $"Set {requiredEnvironmentVariable} to run this integration test.";
    }
}
