using System.Collections.Concurrent;
using System.Diagnostics;
using PosApi.Data;
using SqlKata.Execution;

namespace PosApi.Tenancy;

/// <summary>Live figures read from one client's own database.</summary>
public sealed record ClientFigures
{
    public bool Reachable { get; init; }
    public string? Error { get; init; }
    public long Milliseconds { get; init; }
    public int Branches { get; init; }
    public int Users { get; init; }
    public int UsersActive { get; init; }
    public long Transactions30d { get; init; }
    public long TransactionsPrev30d { get; init; }
    public long TransactionsToday { get; init; }
    public long SalesH30d { get; init; }
    public long SalesHPrev30d { get; init; }
    public DateTime? LastTransactionAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public DateTime? FirstSaleAt { get; init; }
    public long Requests24h { get; init; }
    public long ServerErrors24h { get; init; }
    public double ApiErrorRate => Requests24h == 0 ? 0 : Math.Round(ServerErrors24h * 100.0 / Requests24h, 2);
    public long? DatabaseBytes { get; init; }
    public long SchemaVersion { get; init; }
    public bool RestaurantOpen { get; init; }
    public DateTime ReadAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Reads every client's database in parallel (a timeout each, cached for a minute), so the console's lists and totals
/// are real numbers and one slow or broken client never holds up the others.
/// </summary>
public sealed class ClientMetrics(IDb db)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);
    private readonly ConcurrentDictionary<long, ClientFigures> _cache = new();

    public void Forget(long tenantId) => _cache.TryRemove(tenantId, out _);

    public async Task<Dictionary<long, ClientFigures>> ForAllAsync(IEnumerable<TenantInfo> tenants, bool fresh, CancellationToken ct)
    {
        var list = tenants.ToList();
        var results = await Task.WhenAll(list.Select(t => ForAsync(t, fresh, ct)));
        return list.Zip(results).ToDictionary(x => x.First.Id, x => x.Second);
    }

    public async Task<ClientFigures> ForAsync(TenantInfo tenant, bool fresh, CancellationToken ct)
    {
        if (!fresh && _cache.TryGetValue(tenant.Id, out var hit) && DateTime.UtcNow - hit.ReadAtUtc < CacheFor) return hit;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var figures = await Task.Run(() => ReadAsync(tenant, cts.Token), cts.Token).ContinueWith(t =>
            t.IsCompletedSuccessfully ? t.Result : new ClientFigures { Reachable = false, Error = t.Exception?.GetBaseException().Message ?? "timed out" }, CancellationToken.None);
        _cache[tenant.Id] = figures;
        return figures;
    }

    private async Task<ClientFigures> ReadAsync(TenantInfo tenant, CancellationToken ct)
    {
        TenantContext.Current = tenant;
        var watch = Stopwatch.StartNew();
        try
        {
            await using var conn = await db.OpenAsync(ct);
            var q = db.Query(conn);
            var now = DateTime.UtcNow;
            var d30 = now.AddDays(-30); var d60 = now.AddDays(-60); var today = now.Date; var d1 = now.AddHours(-24);

            async Task<long> Count(Func<SqlKata.Query> make) { try { return await make().CountAsync<long>(cancellationToken: ct); } catch { return 0; } }
            async Task<long> Sum(Func<SqlKata.Query> make, string column) { try { return await make().SumAsync<long?>(column, cancellationToken: ct) ?? 0; } catch { return 0; } }
            async Task<DateTime?> Max(Func<SqlKata.Query> make, string column) { try { return await make().MaxAsync<DateTime?>(column, cancellationToken: ct); } catch { return null; } }
            async Task<DateTime?> Min(Func<SqlKata.Query> make, string column) { try { return await make().MinAsync<DateTime?>(column, cancellationToken: ct); } catch { return null; } }

            SqlKata.Query Live() => q.Query("sales.sale").Where("status", "<>", "voided");
            long? size;
            try { size = await Dapper.SqlMapper.ExecuteScalarAsync<long?>(conn, db.Provider.Dialect.DatabaseSizeBytes); } catch { size = null; }

            return new ClientFigures
            {
                Reachable = true,
                Branches = (int)await Count(() => q.Query("core.branch")),
                Users = (int)await Count(() => q.Query("core.app_user")),
                UsersActive = (int)await Count(() => q.Query("core.app_user").Where("status", "active")),
                Transactions30d = await Count(() => Live().Where("sold_at_utc", ">=", d30)),
                TransactionsPrev30d = await Count(() => Live().Where("sold_at_utc", ">=", d60).Where("sold_at_utc", "<", d30)),
                TransactionsToday = await Count(() => Live().Where("sold_at_utc", ">=", today)),
                SalesH30d = await Sum(() => Live().Where("sold_at_utc", ">=", d30), "total_h"),
                SalesHPrev30d = await Sum(() => Live().Where("sold_at_utc", ">=", d60).Where("sold_at_utc", "<", d30), "total_h"),
                LastTransactionAt = Utc(await Max(() => q.Query("sales.sale"), "sold_at_utc")),
                FirstSaleAt = Utc(await Min(() => q.Query("sales.sale"), "sold_at_utc")),
                LastLoginAt = Utc(await Max(() => q.Query("core.app_user"), "last_login_at_utc")),
                Requests24h = await Count(() => q.Query("audit.request_timing").Where("occurred_at_utc", ">=", d1)),
                ServerErrors24h = await Count(() => q.Query("audit.request_timing").Where("occurred_at_utc", ">=", d1).Where("status_code", ">=", 500)),
                DatabaseBytes = size,
                SchemaVersion = await Latest(q, ct),
                RestaurantOpen = await Count(() => q.Query("sales.restaurant_session").Where("status", "open")) > 0,
                Milliseconds = watch.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            return new ClientFigures { Reachable = false, Error = ex.Message, Milliseconds = watch.ElapsedMilliseconds };
        }
        finally { TenantContext.Current = null; }
    }

    private static async Task<long> Latest(QueryFactory q, CancellationToken ct)
    {
        try { return await q.Query("VersionInfo").MaxAsync<long>("Version", cancellationToken: ct); } catch { return 0; }
    }

    private static DateTime? Utc(DateTime? d) => d is null ? null : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);
}
