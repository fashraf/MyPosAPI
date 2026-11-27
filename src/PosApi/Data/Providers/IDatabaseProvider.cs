using System.Data.Common;
using FluentMigrator.Runner;
using SqlKata.Compilers;

namespace PosApi.Data.Providers;

/// <summary>
/// Everything engine-specific lives behind this interface. Features never see it directly:
/// they use <see cref="IDb"/> (connections, transactions, the query builder) and repositories.
/// Adding an engine (MySQL, SQLite for tests, …) = one new class implementing this.
/// </summary>
public interface IDatabaseProvider
{
    string Name { get; }
    DbConnection CreateConnection(string connectionString);

    /// <summary>SqlKata compiler: queries written once with the query builder become this engine's SQL.</summary>
    Compiler Compiler { get; }

    /// <summary>The few statements the query builder cannot express portably.</summary>
    ISqlDialect Dialect { get; }

    /// <summary>Tells FluentMigrator which engine the C# migrations are generated for.</summary>
    void AddMigrationEngine(IMigrationRunnerBuilder runner);
}

/// <summary>
/// Engine-specific SQL, kept tiny on purpose. Anything that can be written with SqlKata or plain ANSI SQL
/// does not belong here.
/// </summary>
public interface ISqlDialect
{
    /// <summary>Atomically adds 1 to number_series.last_value and returns the new value (row stays locked
    /// until the transaction ends, so numbers are gapless). Parameters: @Key, @Scope.</summary>
    string IncrementNumberSeries { get; }

    /// <summary>Turns "INSERT INTO t (…) VALUES (…)" into a statement that also returns the new identity value.</summary>
    string InsertReturningId(string insertSql, string idColumn);

    /// <summary>Current UTC timestamp expression.</summary>
    string UtcNow { get; }

    /// <summary>Size of the connected database in bytes (POSSA usage figures).</summary>
    string DatabaseSizeBytes { get; }

    /// <summary>Creates an empty database (name already checked: lowercase letters, digits, underscore) owned by the user.</summary>
    string CreateDatabase(string name, string owner);
}
