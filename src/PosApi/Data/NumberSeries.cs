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
    Task EnsureAtLeastAsync(DbConnection connection, DbTransaction tx, string key, long value, string scope = "*");
}

public sealed class NumberSeries(IDb db) : INumberSeries
{
    /// <summary>First number handed out = start + 1 (invoice 100001, credit note 500001, receipt 700001).</summary>
    /// <summary>The value BEFORE the first number: invoice 0 means the first invoice is 1.</summary>
    public static readonly IReadOnlyDictionary<string, long> Starts = new Dictionary<string, long>
    {
        ["invoice"] = 0, ["credit_note"] = 500000, ["receipt"] = 700000, ["session"] = 1000, ["employee"] = 1000,
        ["branch"] = 0, ["user"] = 0, ["catalog_item"] = 0, ["category"] = 0, ["print_group"] = 0,
        ["session_movement"] = 0, ["session_audit"] = 0,
    };

    /// <summary>After rows were inserted with explicit ids (the seed), make sure the next number is above them.</summary>
    public async Task EnsureAtLeastAsync(DbConnection connection, DbTransaction tx, string key, long value, string scope = "*")
    {
        var q = db.Query(connection);
        var row = await q.Query("number_series").Where("series_key", key).Where("scope_id", scope).FirstOrDefaultAsync(tx);
        if (row is null)
        {
            var start = Starts.TryGetValue(key, out var s) ? s : 0;
            await q.Query("number_series").InsertAsync(new { series_key = key, scope_id = scope, last_value = Math.Max(start, value) }, tx);
        }
        else if (Convert.ToInt64(((IDictionary<string, object?>)row)["last_value"]) < value)
        {
            await q.Query("number_series").Where("series_key", key).Where("scope_id", scope).UpdateAsync(new { last_value = value }, tx);
        }
    }

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
