using System.Data.Common;
using FluentMigrator.Runner;
using Npgsql;
using SqlKata.Compilers;

namespace PosApi.Data.Providers;

public sealed class PostgresProvider : IDatabaseProvider, ISqlDialect
{
    // Store DateTime values as-is in "timestamp" columns (UTC by convention, like SQL Server's DATETIME2).
    static PostgresProvider() => AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

    public string Name => "postgres";
    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);
    public Compiler Compiler { get; } = new PostgresCompiler();
    public ISqlDialect Dialect => this;
    public void AddMigrationEngine(IMigrationRunnerBuilder runner) => runner.AddPostgres();

    public string IncrementNumberSeries =>
        "UPDATE number_series SET last_value = last_value + 1 WHERE series_key = @Key AND scope_id = @Scope RETURNING last_value";
    public string InsertReturningId(string insertSql, string idColumn) => string.Concat(insertSql, " RETURNING ", idColumn);
    public string UtcNow => "(now() at time zone 'utc')";

    public string DatabaseSizeBytes => "SELECT pg_database_size(current_database())";
    public string CreateDatabase(string name, string owner) => $"CREATE DATABASE \"{name}\" OWNER \"{owner}\"";
}
