namespace CommandHub.Application;

public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";
    public int GlobalConcurrency { get; set; } = 10;
    public int PerServerConcurrency { get; set; } = 3;
    public int PerUserConcurrency { get; set; } = 3;
    public int QueueCapacity { get; set; } = 100;
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public int MaximumTimeoutSeconds { get; set; } = 3600;
    public long MaximumOutputBytes { get; set; } = 10 * 1024 * 1024;
    public int OutputChunkBytes { get; set; } = 16 * 1024;
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public bool RecoverOnStartup { get; set; } = true;
}

public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public bool AllowCriticalCommands { get; set; }
    public bool StoreEncryptedOriginalCommands { get; set; }
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public bool ApplyMigrationsOnStartup { get; set; }
}

public static class SecurityConfirmation
{
    public const string CriticalPhrase = "I UNDERSTAND THIS COMMAND MAY CAUSE IRREVERSIBLE DAMAGE";
}
