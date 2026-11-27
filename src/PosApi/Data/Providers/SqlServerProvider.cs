using System.Data.Common;
using FluentMigrator.Runner;
using Microsoft.Data.SqlClient;
using SqlKata.Compilers;

namespace PosApi.Data.Providers;

public sealed class SqlServerProvider : IDatabaseProvider, ISqlDialect
{
    public string Name => "sqlserver";
    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);
    public Compiler Compiler { get; } = new SqlServerCompiler { UseLegacyPagination = false };
    public ISqlDialect Dialect => this;
    public void AddMigrationEngine(IMigrationRunnerBuilder runner) => runner.AddSqlServer();

    public string IncrementNumberSeries =>
        "UPDATE number_series SET last_value = last_value + 1 OUTPUT inserted.last_value WHERE series_key = @Key AND scope_id = @Scope";
    public string InsertReturningId(string insertSql, string idColumn) =>
        insertSql.Replace(" VALUES", string.Concat(" OUTPUT inserted.", idColumn, " VALUES"), StringComparison.OrdinalIgnoreCase);
    public string UtcNow => "SYSUTCDATETIME()";

    public string CreateDatabase(string name, string owner) => $"CREATE DATABASE [{name}]";
    public string DatabaseSizeBytes => "SELECT CAST(SUM(CAST(size AS BIGINT)) * 8192 AS BIGINT) FROM sys.database_files";
}
