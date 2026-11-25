using System.Data.Common;
using PosApi.Data.Providers;
using SqlKata.Execution;

namespace PosApi.Data;

/// <summary>
/// The only way features reach the database. Engine-neutral: the configured provider decides the driver,
/// the SQL dialect and the query compiler.
/// </summary>
public interface IDb
{
    IDatabaseProvider Provider { get; }
    Task<DbConnection> OpenAsync(CancellationToken ct);

    /// <summary>Query builder bound to an open connection (and optional transaction):
    /// <c>db.Query(conn).Query("sale").Where("sale_id", id).FirstOrDefaultAsync&lt;SaleRow&gt;()</c>.</summary>
    QueryFactory Query(DbConnection connection);

    /// <summary>Runs work in one transaction; commits on success, rolls back on any exception.</summary>
    Task<T> InTransactionAsync<T>(Func<DbConnection, DbTransaction, Task<T>> work, CancellationToken ct);
}

public sealed class Db : IDb
{
    private readonly string _connectionString;

    public Db(IDatabaseProvider provider, IConfiguration configuration)
    {
        Provider = provider;
        _connectionString = configuration.GetConnectionString("PosMain")
            ?? throw new InvalidOperationException("Connection string 'PosMain' is missing (ConnectionStrings__PosMain).");
    }

    public IDatabaseProvider Provider { get; }

    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        /* Timed: every statement a request runs is measured and listed on the API performance page. */
        var connection = new Profiling.TimedConnection(Provider.CreateConnection(_connectionString));
        await connection.OpenAsync(ct);
        return connection;
    }

    public QueryFactory Query(DbConnection connection) => new(connection, Provider.Compiler);

    public async Task<T> InTransactionAsync<T>(Func<DbConnection, DbTransaction, Task<T>> work, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            var result = await work(connection, tx);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
