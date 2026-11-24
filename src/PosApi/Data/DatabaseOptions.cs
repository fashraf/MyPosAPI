namespace PosApi.Data;

/// <summary>
/// Which database engine the API talks to. Chosen by configuration only — never by code:
///   Database:Provider = postgres | sqlserver   (environment variable Database__Provider)
///   ConnectionStrings:PosMain = the connection string for that engine
/// Moving to another engine = change these two settings and run "PosApi --migrate". No code changes.
/// </summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public string Provider { get; set; } = "sqlserver";
}
