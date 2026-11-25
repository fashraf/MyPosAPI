using Dapper;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;
using SqlKata;
using SqlKata.Execution;

namespace PosApi.Features.Settings;

/// <summary>
/// Audit trail and API performance, on the engine-neutral layer (IDb + SqlKata, no engine-specific SQL).
/// Response shapes are unchanged from the SQL Server version: columns are aliased to the PascalCase names the
/// front end already reads (AuditId, ApiPath, TotalMs …).
/// </summary>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit")
            .WithTags("Audit")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        group.MapGet("", ListAsync).RequireAuthorization(PermissionPolicy.For("audit.view"));
        group.MapGet("/{auditId:long}/detail", DetailAsync).RequireAuthorization(PermissionPolicy.For("audit.view"));
        group.MapGet("/{auditId:long}", GetAsync).RequireAuthorization(PermissionPolicy.For("audit.view"));
        group.MapGet("/filters", FiltersAsync).RequireAuthorization(PermissionPolicy.For("audit.view"));
        group.MapGet("/performance", PerformanceAsync).RequireAuthorization(PermissionPolicy.For("audit.performance"));
        group.MapGet("/performance/slow", SlowAsync).RequireAuthorization(PermissionPolicy.For("audit.performance"));
        group.MapGet("/performance/nplusone", NPlusOneAsync).RequireAuthorization(PermissionPolicy.For("audit.performance"));
    }

    private static readonly string[] EntryColumns =
    {
        "audit_id as AuditId", "user_id as UserId", "username as Username", "role_id as RoleId",
        "action as Action", "module as Module", "entity_type as EntityType", "entity_id as EntityId",
        "api_path as ApiPath", "http_method as HttpMethod", "current_page as CurrentPage",
        "previous_page as PreviousPage", "branch_id as BranchId", "result as Result",
        "status_code as StatusCode", "error_message as ErrorMessage", "ip_address as IpAddress",
        "correlation_id as CorrelationId", "occurred_at_utc as OccurredAtUtc"
    };

    private static readonly string[] EntryFullColumns = EntryColumns
        .Concat(new[] { "entity_type as EntityType", "old_value as OldValue", "new_value as NewValue", "details as Details", "user_agent as UserAgent" })
        .Distinct().ToArray();

    /// <summary>
    /// The audit trail, filtered. There is no write endpoint on purpose: entries come from the middleware,
    /// from what actually happened. An API that let a caller post its own rows would make the trail worthless.
    /// </summary>
    private static async Task<IResult> ListAsync(
        IDb db,
        string? userId, string? action, string? module, string? entityId,
        string? result, string? search, DateOnly? from, DateOnly? to,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var paging = new PageQuery { Page = page <= 0 ? 1 : page, PageSize = pageSize };
        await using var connection = await db.OpenAsync(ct);
        var q = db.Query(connection);

        Query Filtered()
        {
            var x = q.Query("audit.audit_entry");
            if (!string.IsNullOrWhiteSpace(userId)) x.Where("user_id", userId);
            if (!string.IsNullOrWhiteSpace(action)) x.Where("action", action);
            if (!string.IsNullOrWhiteSpace(module)) x.Where("module", module);
            if (!string.IsNullOrWhiteSpace(entityId)) x.Where("entity_id", entityId);
            if (!string.IsNullOrWhiteSpace(result)) x.Where("result", result);
            if (from is { } f) x.Where("occurred_at_utc", ">=", f.ToDateTime(TimeOnly.MinValue));
            if (to is { } t) x.Where("occurred_at_utc", "<", t.AddDays(1).ToDateTime(TimeOnly.MinValue));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = $"%{search.Trim()}%";
                x.Where(w => w.WhereLike("username", s).OrWhereLike("api_path", s)
                              .OrWhereLike("entity_id", s).OrWhereLike("current_page", s));
            }
            return x;
        }

        var total = await Filtered().CountAsync<int>(cancellationToken: ct);
        var rows = (await Filtered()
                .Select(EntryColumns)
                .SelectRaw("CASE WHEN old_value IS NOT NULL THEN 1 ELSE 0 END AS \"HasOldValue\"")
                .SelectRaw("CASE WHEN new_value IS NOT NULL THEN 1 ELSE 0 END AS \"HasNewValue\"")
                .OrderByDesc("occurred_at_utc").OrderByDesc("audit_id")
                .Offset(paging.Offset).Limit(paging.PageSize)
                .GetAsync(cancellationToken: ct)).ToList();

        return Results.Ok(new PagedResult<object>
        {
            Items = rows.Cast<object>().ToList(),
            Page = paging.Page,
            PageSize = paging.PageSize,
            TotalCount = total
        });
    }

    private static async Task<IResult> GetAsync(long auditId, IDb db, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var entry = await db.Query(connection).Query("audit.audit_entry").Select(EntryFullColumns)
            .Where("audit_id", auditId).FirstOrDefaultAsync(cancellationToken: ct);
        return entry is null ? Results.NotFound() : Results.Ok(entry);
    }

    /// <summary>One entry with everything it touched: tables, rows, fields before/after, and the request timing.</summary>
    private static async Task<IResult> DetailAsync(long auditId, IDb db, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var q = db.Query(connection);

        var entry = await q.Query("audit.audit_entry").Select(EntryFullColumns)
            .Where("audit_id", auditId).FirstOrDefaultAsync(cancellationToken: ct);
        if (entry is null) return Results.NotFound();

        var changes = (await q.Query("audit.audit_change")
            .Select("change_id as ChangeId", "schema_name as SchemaName", "table_name as TableName",
                    "operation as Operation", "primary_key as PrimaryKey", "foreign_key as ForeignKey",
                    "foreign_table as ForeignTable", "column_name as ColumnName", "old_value as OldValue",
                    "new_value as NewValue", "rows_affected as RowsAffected")
            .Where("audit_id", auditId)
            .OrderBy("schema_name", "table_name", "change_id")
            .GetAsync(cancellationToken: ct)).ToList();

        var tables = changes
            .Select(c => (IDictionary<string, object?>)c)
            .GroupBy(c => (Name: $"{c["SchemaName"]}.{c["TableName"]}", Op: c["Operation"]?.ToString()))
            .OrderBy(g => g.Key.Name)
            .Select(g => new
            {
                FullName = g.Key.Name,
                Operation = g.Key.Op,
                Changes = g.Count(),
                RowsAffected = g.Max(c => c["RowsAffected"] is null ? 0 : Convert.ToInt32(c["RowsAffected"]))
            })
            .ToList();

        var correlation = ((IDictionary<string, object?>)entry)["CorrelationId"]?.ToString();
        var timing = correlation is null ? null : await q.Query("audit.request_timing")
            .Select("total_ms as TotalMs", "database_ms as DatabaseMs", "query_count as QueryCount",
                    "status_code as StatusCode", "succeeded as Succeeded")
            .Where("correlation_id", correlation)
            .FirstOrDefaultAsync(cancellationToken: ct);

        return Results.Ok(new { entry, changes, tables, timing });
    }

    /// <summary>The distinct values behind each filter, so the page can offer them.</summary>
    private static async Task<IResult> FiltersAsync(IDb db, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var q = db.Query(connection);

        var actions = (await q.Query("audit.audit_entry").Select("action").Distinct().OrderBy("action")
            .GetAsync<string>(cancellationToken: ct)).ToList();
        var modules = (await q.Query("audit.audit_entry").Select("module").Distinct().OrderBy("module")
            .GetAsync<string>(cancellationToken: ct)).ToList();
        var users = (await q.Query("audit.audit_entry").Select("user_id as UserId", "username as Username")
            .Distinct().WhereNotNull("user_id").OrderBy("username")
            .GetAsync(cancellationToken: ct)).ToList();

        return Results.Ok(new { actions, modules, users });
    }

    /// <summary>Per-endpoint speed with the QA rating bands, plus a summary for the period.</summary>
    private static async Task<IResult> PerformanceAsync(IDb db, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var q = db.Query(connection);

        Query Period()
        {
            var x = q.Query("audit.request_timing");
            if (from is { } f) x.Where("occurred_at_utc", ">=", f.ToDateTime(TimeOnly.MinValue));
            if (to is { } t) x.Where("occurred_at_utc", "<", t.AddDays(1).ToDateTime(TimeOnly.MinValue));
            return x;
        }

        var stats = (await Period()
            .Select("api_path as ApiPath", "http_method as HttpMethod")
            .SelectRaw("COUNT(*) AS \"Calls\"")
            .SelectRaw("AVG(total_ms) AS \"AvgMs\"")
            .SelectRaw("MIN(total_ms) AS \"MinMs\"")
            .SelectRaw("MAX(total_ms) AS \"MaxMs\"")
            .SelectRaw("AVG(database_ms) AS \"AvgDatabaseMs\"")
            .SelectRaw("SUM(CASE WHEN succeeded = ? THEN 1 ELSE 0 END) AS \"Failures\"", false)
            .GroupBy("api_path", "http_method")
            .GetAsync(cancellationToken: ct)).ToList();

        // Rating and failure % are computed here, identically on every engine.
        var endpoints = stats
            .Select(r => (IDictionary<string, object?>)r)
            .Select(r =>
            {
                var calls = Convert.ToInt64(r["Calls"]);
                var avg = Convert.ToDouble(r["AvgMs"] ?? 0);
                var failures = Convert.ToInt64(r["Failures"] ?? 0);
                return new
                {
                    ApiPath = r["ApiPath"], HttpMethod = r["HttpMethod"], Calls = calls,
                    AvgMs = (long)Math.Round(avg),
                    MinMs = Convert.ToInt64(r["MinMs"] ?? 0), MaxMs = Convert.ToInt64(r["MaxMs"] ?? 0),
                    AvgDatabaseMs = (long)Math.Round(Convert.ToDouble(r["AvgDatabaseMs"] ?? 0)),
                    Failures = failures,
                    FailurePercent = calls == 0 ? 0 : Math.Round(100.0 * failures / calls, 1),
                    Rating = Rate(avg)
                };
            })
            .OrderByDescending(e => e.AvgMs)
            .ToList();

        var summary = await Period()
            .SelectRaw("COUNT(*) AS \"TotalCalls\"")
            .SelectRaw("AVG(total_ms) AS \"AvgMs\"")
            .SelectRaw("MAX(total_ms) AS \"WorstMs\"")
            .SelectRaw("SUM(CASE WHEN succeeded = ? THEN 1 ELSE 0 END) AS \"Failures\"", false)
            .SelectRaw("SUM(CASE WHEN total_ms >= 2000 THEN 1 ELSE 0 END) AS \"CriticalCalls\"")
            .SelectRaw("SUM(CASE WHEN total_ms >= 1000 AND total_ms < 2000 THEN 1 ELSE 0 END) AS \"SlowCalls\"")
            .FirstOrDefaultAsync(cancellationToken: ct);

        /* AVG comes back as an unrounded decimal on some engines (18.1623…): whole milliseconds on the screen. */
        if (summary is IDictionary<string, object?> sum && sum.TryGetValue("AvgMs", out var avgMs) && avgMs is not null)
            sum["AvgMs"] = (long)Math.Round(Convert.ToDouble(avgMs));
        return Results.Ok(new { endpoints, summary });
    }

    /// <summary>The QA bands from the test plan.</summary>
    internal static string Rate(double avgMs) => avgMs switch
    {
        < 100 => "Excellent",
        < 300 => "Good",
        < 1000 => "Acceptable",
        < 2000 => "Slow",
        _ => "Critical"
    };

    /// <summary>The slowest individual requests. Seconds are derived, never stored.</summary>
    private static async Task<IResult> SlowAsync(IDb db, int thresholdMs = 300, int take = 50, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = (await db.Query(connection).Query("audit.request_timing")
            .Select("correlation_id as CorrelationId", "username as Username", "api_path as ApiPath",
                    "http_method as HttpMethod", "current_page as CurrentPage", "previous_page as PreviousPage",
                    "total_ms as TotalMs", "database_ms as DatabaseMs", "query_count as QueryCount",
                    "status_code as StatusCode", "succeeded as Succeeded", "error_message as ErrorMessage",
                    "occurred_at_utc as OccurredAtUtc")
            .Where("total_ms", ">=", thresholdMs <= 0 ? 300 : thresholdMs)
            .OrderByDesc("total_ms")
            .Limit(take <= 0 ? 50 : Math.Min(take, 200))
            .GetAsync(cancellationToken: ct)).ToList();

        var requests = rows.Select(r => (IDictionary<string, object?>)r).Select(r =>
        {
            r["TotalSeconds"] = Math.Round(Convert.ToDouble(r["TotalMs"] ?? 0) / 1000.0, 3);
            r["DatabaseSeconds"] = Math.Round(Convert.ToDouble(r["DatabaseMs"] ?? 0) / 1000.0, 3);
            return r;
        }).ToList();

        // Per-statement timing is not recorded on this layer yet; the list stays empty rather than inventing data.
        return Results.Ok(new { requests, queries = Array.Empty<object>() });
    }

    /// <summary>Suspected N+1 patterns need per-statement timing, which this layer does not record yet.</summary>
    private static Task<IResult> NPlusOneAsync(CancellationToken ct) =>
        Task.FromResult(Results.Ok(Array.Empty<object>()));
}
