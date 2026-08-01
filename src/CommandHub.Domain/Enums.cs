namespace CommandHub.Domain;

public enum ServerEnvironment { Development, Testing, Staging, Production, Other }
public enum CredentialType { Password, PrivateKey }
public enum PermissionLevel { Viewer, Operator, Administrator }
public enum ExecutionStatus { Pending, Queued, Running, Succeeded, Failed, Cancelled, TimedOut, Rejected, ConnectionFailed }
public enum CommandSource { Web, Template, History, Api, Imported }
public enum RiskLevel { Low, Medium, High, Critical }
public enum OutputStreamType { StandardOutput, StandardError, System }
public enum TemplateParameterType { Text, Number, Boolean, Choice, Path, Host, Port, Secret }
public enum TemplateEscapeMode { Raw, ShellArgument, Integer, BooleanFlag }

public static class SystemRoles
{
    public const string SystemAdministrator = nameof(SystemAdministrator);
    public const string Operator = nameof(Operator);
    public const string Viewer = nameof(Viewer);
    public const string Auditor = nameof(Auditor);

    public static readonly string[] All = [SystemAdministrator, Operator, Viewer, Auditor];
}
