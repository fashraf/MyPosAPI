namespace PosApi.Data.Providers;

public static class DatabaseProviders
{
    /// <summary>Maps the configured name to an engine. Unknown names fail at startup with a clear message.</summary>
    public static IDatabaseProvider Resolve(string? name) => (name ?? "sqlserver").Trim().ToLowerInvariant() switch
    {
        "postgres" or "postgresql" or "npgsql" => new PostgresProvider(),
        "sqlserver" or "mssql" => new SqlServerProvider(),
        var other => throw new InvalidOperationException(
            $"Database:Provider '{other}' is not supported. Use 'postgres' or 'sqlserver'.")
    };
}
