using System.Data.Common;
using Dapper;
using SqlKata.Execution;

namespace PosApi.Data;

/// <summary>
/// Business numbers (invoice, credit note, receipt, session, employee). Numeric, assigned only here,
/// inside the caller's transaction, so a rolled-back sale gives its number back (no gaps).
/// </summary>
public interface INumberSeries
{
    Task<long> NextAsync(DbConnection connection, DbTransaction tx, string key, string scope = "*");
}

public sealed class NumberSeries(IDb db) : INumberSeries
{
    /// <summary>First number handed out = start + 1 (invoice 100001, credit note 500001, receipt 700001).</summary>
    public static readonly IReadOnlyDictionary<string, long> Starts = new Dictionary<string, long>
    {
        ["invoice"] = 100000, ["credit_note"] = 500000, ["receipt"] = 700000, ["session"] = 1000, ["employee"] = 1000, ["branch"] = 99,
    };

    public async Task<long> NextAsync(DbConnection connection, DbTransaction tx, string key, string scope = "*")
    {
        var sql = db.Provider.Dialect.IncrementNumberSeries;
        var next = await connection.QuerySingleOrDefaultAsync<long?>(sql, new { Key = key, Scope = scope }, tx);
        if (next is { } value) return value;

        // First use of this series/scope: create the row, then take the first number.
        var start = Starts.TryGetValue(key, out var s) ? s : 0;
        await db.Query(connection).Query("number_series")
            .InsertAsync(new { series_key = key, scope_id = scope, last_value = start }, tx);
        return await connection.QuerySingleAsync<long>(sql, new { Key = key, Scope = scope }, tx);
    }
}
