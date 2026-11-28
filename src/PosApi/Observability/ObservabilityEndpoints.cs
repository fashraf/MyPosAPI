using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using PosApi.Auth;
using PosApi.Data;
using PosApi.Features.SuperAdmin;
using PosApi.Tenancy;
using SqlKata.Execution;

namespace PosApi.Observability;

/// <summary>
/// Ayn's health, performance, errors and activity. Live figures read from every client's own audit tables
/// (request_timing, query_timing, audit_entry, login_attempt) plus control.error_log / control.ui_event.
/// </summary>
public static partial class ObservabilityEndpoints
{
    public const int SlowApiMs = 1000;
    public const int SlowSqlMs = 200;
    private static readonly DateTime Started = DateTime.UtcNow;

    public sealed record TelemetryBody(string? Kind, string? Source, string? App, string? Message, string? Detail, string? Url, string? Method,
        int? Status, string? Page, string? Label, int? DurationMs);
    public sealed record DatabaseBody(string? Name);

    public static void MapObservabilityEndpoints(this IEndpointRouteBuilder app)
    {
        /* Front ends report errors and screen events here (POS on a client address, Ayn on the admin address). */
        app.MapPost("/api/telemetry", (TelemetryBody b, ErrorLog log, ICurrentUser user, HttpContext http) =>
        {
            var tenant = TenantContext.Current?.Id;
            var appName = ErrorLog.Cut(b.App, 20) ?? (TenantContext.Admin ? "ayn" : "pos");
            var who = user.IsAuthenticated ? user.Username : null;
            if (b.Kind == "error")
                log.Report(new ErrorReport(b.Source is "network" ? "network" : "frontend", appName, b.Message ?? "(no message)", b.Detail, b.Url ?? b.Page,
                    b.Method, b.Status, who, http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString(), tenant));
            else
                log.Track(new UiEventReport(appName, b.Kind is "view" or "action" or "slow" ? b.Kind : "action", b.Page, b.Label, b.DurationMs, who, tenant));
            return Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("standard").WithTags("Telemetry");

        var g = app.MapGroup("/api/sa").WithTags("Ayn observability").RequireRateLimiting("standard")
            .RequireAuthorization(p => p.RequireRole(SuperAdminEndpoints.Role));

        g.MapGet("/system", SystemAsync);
        g.MapGet("/perf/api", ApiPerfAsync);
        g.MapGet("/perf/sql", SqlPerfAsync);
        g.MapGet("/errors", ErrorsAsync);
        g.MapPost("/errors/{id:long}/resolve", ResolveAsync);
        g.MapGet("/ui-events", UiEventsAsync);
        g.MapGet("/client-activity", ClientActivityAsync);
        g.MapGet("/logins", LoginsAsync);
        g.MapGet("/analytics/activity", ActivityAnalyticsAsync);
        g.MapPost("/databases", CreateDatabaseAsync);
        g.MapGet("/platform/users", AllUsersAsync);
    }

    /* ------------------------------------------------------------------ fan-out over client databases */

    internal sealed record ClientRef(long Id, string Name, TenantInfo Tenant);

    internal static async Task<List<ClientRef>> ClientsAsync(ControlStore control, long? only, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var q = control.Query(c).Query("control.tenant").Where("status", "<>", "setup");
        if (only is not null) q = q.Where("tenant_id", only);
        return (await q.GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>()
            .Select(r => new ClientRef(Convert.ToInt64(r["tenant_id"]), (string)r["name"]!, control.ToTenant(r))).ToList();
    }

    /// <summary>Runs the same read in every client's database (in parallel, 8 s each); a client that fails is skipped and named.</summary>
    internal static async Task<(List<(ClientRef Client, T Value)> Ok, List<object> Failed)> EachAsync<T>(IDb db, List<ClientRef> clients,
        Func<ClientRef, DbConnection, Task<T>> read, CancellationToken ct)
    {
        var results = await Task.WhenAll(clients.Select(async cl =>
        {
            TenantContext.Current = cl.Tenant;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                await using var conn = await db.OpenAsync(cts.Token);
                return (cl, value: await read(cl, conn), error: (string?)null);
            }
            catch (Exception ex) { return (cl, value: default(T)!, error: ex.Message); }
            finally { TenantContext.Current = null; }
        }));
        return (results.Where(r => r.error is null).Select(r => (r.cl, r.value)).ToList(),
                results.Where(r => r.error is not null).Select(r => (object)new { clientId = r.cl.Id.ToString(CultureInfo.InvariantCulture), client = r.cl.Name, error = r.error }).ToList());
    }

    internal static async Task<List<IDictionary<string, object?>>> Rows(IDb db, DbConnection conn, Func<SqlKata.Execution.QueryFactory, SqlKata.Query> build, CancellationToken ct) =>
        (await build(db.Query(conn)).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();

    /* ------------------------------------------------------------------ system health */

    private static async Task<IResult> SystemAsync(ControlStore control, IDb db, IWebHostEnvironment env, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var watch = Stopwatch.StartNew();
        long controlBytes = 0; bool controlOk = true;
        try
        {
            await using var c = await control.OpenAsync(ct);
            controlBytes = await c.ExecuteScalarAsync<long>(control.Provider.Dialect.DatabaseSizeBytes);
        }
        catch { controlOk = false; }
        var controlMs = watch.ElapsedMilliseconds;

        var clients = controlOk ? await ClientsAsync(control, null, ct) : [];
        var (ok, failed) = await EachAsync(db, clients, async (cl, conn) =>
        {
            var sw = Stopwatch.StartNew();
            var size = await conn.ExecuteScalarAsync<long>(db.Provider.Dialect.DatabaseSizeBytes);
            var ms = sw.ElapsedMilliseconds;
            var timings = await Rows(db, conn, f => f.Query("audit.request_timing").Where("occurred_at_utc", ">=", since)
                .Select("api_path", "http_method", "total_ms", "status_code", "username", "occurred_at_utc").OrderByDesc("occurred_at_utc").Limit(20000), ct);
            var queries = await Rows(db, conn, f => f.Query("audit.query_timing").Where("occurred_at_utc", ">=", since)
                .Select("duration_ms", "command_text", "occurred_at_utc").OrderByDesc("occurred_at_utc").Limit(20000), ct);
            var logins = await LoginRows(db, conn, since.AddDays(-6), 2000, ct);
            var lastWrite = await Rows(db, conn, f => f.Query("audit.audit_entry").WhereNotIn("action", new[] { "view", "read", "login", "logout" })
                .Select("username", "action", "module", "entity_type", "occurred_at_utc").OrderByDesc("audit_id").Limit(1), ct);
            return new { size, ms, timings, queries, logins, lastWrite = lastWrite.FirstOrDefault() };
        }, ct);

        var allTimings = ok.SelectMany(x => x.Value.timings.Select(t => (x.Client, t))).ToList();
        var allQueries = ok.SelectMany(x => x.Value.queries.Select(q => (x.Client, q))).ToList();
        var allLogins = ok.SelectMany(x => x.Value.logins.Select(l => (x.Client, l))).OrderByDescending(x => Dt(x.l["attempted_at_utc"])).ToList();
        var ms = allTimings.Select(x => (double)Convert.ToInt32(x.t["total_ms"])).OrderBy(v => v).ToList();
        var errors = allTimings.Count(x => Convert.ToInt32(x.t["status_code"]) >= 500);
        var now = DateTime.UtcNow;
        var hours = Enumerable.Range(0, 24).Select(i => now.AddHours(-(23 - i))).Select(h => new DateTime(h.Year, h.Month, h.Day, h.Hour, 0, 0, DateTimeKind.Utc)).ToList();
        object Hourly<TX>(IEnumerable<TX> items, Func<TX, DateTime?> at, Func<IEnumerable<TX>, double> value) => hours.Select(h =>
        {
            var inHour = items.Where(i => at(i) is DateTime d && d >= h && d < h.AddHours(1)).ToList();
            return new { hour = h.ToString("HH:00"), at = h, value = Math.Round(value(inHour), 1) };
        }).ToList();
        var lastRead = allTimings.Where(x => (string)x.t["http_method"]! == "GET").OrderByDescending(x => Dt(x.t["occurred_at_utc"])).FirstOrDefault();
        var writes = ok.Where(x => x.Value.lastWrite is not null).Select(x => (x.Client, w: x.Value.lastWrite!)).OrderByDescending(x => Dt(x.w["occurred_at_utc"])).FirstOrDefault();
        var lastLogin = allLogins.FirstOrDefault(x => (bool)x.l["succeeded"]!);
        var logins24 = allLogins.Where(x => Dt(x.l["attempted_at_utc"]) >= since).ToList();
        var proc = Process.GetCurrentProcess();
        var sqlMs = allQueries.Select(x => Convert.ToDouble(x.q["duration_ms"], CultureInfo.InvariantCulture)).ToList();

        return Results.Ok(new
        {
            readAtUtc = now,
            app = new
            {
                status = "operational", environment = env.EnvironmentName, version = typeof(ObservabilityEndpoints).Assembly.GetName().Version?.ToString(),
                startedAtUtc = Started, uptimeMinutes = Math.Round((now - Started).TotalMinutes), memoryMb = Math.Round(proc.WorkingSet64 / 1048576d),
                threads = proc.Threads.Count, clients = clients.Count, clientsReachable = ok.Count,
            },
            api = new
            {
                status = errors > 0 && ms.Count > 0 && errors * 100.0 / ms.Count > 2 ? "degraded" : "operational",
                requests24h = ms.Count, errors24h = errors, errorRatePct = ms.Count == 0 ? 0 : Math.Round(errors * 100.0 / ms.Count, 2),
                avgMs = ms.Count == 0 ? 0 : Math.Round(ms.Average()), p95Ms = Pct(ms, 0.95), maxMs = ms.Count == 0 ? 0 : ms[^1],
                slow24h = ms.Count(v => v >= SlowApiMs), slowThresholdMs = SlowApiMs,
                requestsByHour = Hourly(allTimings, x => Dt(x.t["occurred_at_utc"]), x => x.Count()),
                latencyByHour = Hourly(allTimings, x => Dt(x.t["occurred_at_utc"]), x => x.Any() ? x.Average(i => Convert.ToInt32(i.t["total_ms"])) : 0),
                errorsByHour = Hourly(allTimings, x => Dt(x.t["occurred_at_utc"]), x => x.Count(i => Convert.ToInt32(i.t["status_code"]) >= 500)),
            },
            database = new
            {
                status = !controlOk ? "down" : failed.Count > 0 ? "degraded" : "operational",
                controlBytes, controlMs, totalBytes = controlBytes + ok.Sum(x => x.Value.size),
                clients = ok.Select(x => new { id = x.Client.Id.ToString(CultureInfo.InvariantCulture), name = x.Client.Name, bytes = x.Value.size, ms = x.Value.ms })
                    .OrderByDescending(x => x.bytes).ToList(),
                unreachable = failed,
                queries24h = sqlMs.Count, avgQueryMs = sqlMs.Count == 0 ? 0 : Math.Round(sqlMs.Average(), 1), slowQueries24h = sqlMs.Count(v => v >= SlowSqlMs),
                slowThresholdMs = SlowSqlMs,
                queriesByHour = Hourly(allQueries, x => Dt(x.q["occurred_at_utc"]), x => x.Count()),
            },
            lastRead = lastRead.t is null ? null : new { at = Dt(lastRead.t["occurred_at_utc"]), client = lastRead.Client.Name, clientId = lastRead.Client.Id.ToString(CultureInfo.InvariantCulture), user = lastRead.t["username"] as string, path = lastRead.t["api_path"] as string },
            lastWrite = writes.w is null ? null : new { at = Dt(writes.w["occurred_at_utc"]), client = writes.Client.Name, clientId = writes.Client.Id.ToString(CultureInfo.InvariantCulture), user = writes.w["username"] as string, what = $"{writes.w["action"]} · {writes.w["module"]}{(writes.w["entity_type"] is string e ? " · " + e : "")}" },
            lastLogin = lastLogin.l is null ? null : new { at = Dt(lastLogin.l["attempted_at_utc"]), client = lastLogin.Client.Name, clientId = lastLogin.Client.Id.ToString(CultureInfo.InvariantCulture), user = lastLogin.l["identifier"] as string, ip = lastLogin.l["ip_address"] as string },
            logins = new
            {
                succeeded24h = logins24.Count(x => (bool)x.l["succeeded"]!), failed24h = logins24.Count(x => !(bool)x.l["succeeded"]!),
                byHour = Hourly(logins24, x => Dt(x.l["attempted_at_utc"]), x => x.Count(i => (bool)i.l["succeeded"]!)),
                recent = allLogins.Take(12).Select(x => new { at = Dt(x.l["attempted_at_utc"]), client = x.Client.Name, clientId = x.Client.Id.ToString(CultureInfo.InvariantCulture), user = x.l["identifier"] as string, ok = (bool)x.l["succeeded"]!, ip = x.l["ip_address"] as string }),
            },
        });
    }

    /* ------------------------------------------------------------------ API performance */

    private static async Task<IResult> ApiPerfAsync(ControlStore control, IDb db, int? hours, long? clientId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 30));
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, clientId, ct), (cl, conn) => Rows(db, conn, f => f.Query("audit.request_timing")
            .Where("occurred_at_utc", ">=", since).Select("api_path", "http_method", "total_ms", "database_ms", "query_count", "status_code", "username", "error_message", "occurred_at_utc")
            .OrderByDesc("occurred_at_utc").Limit(30000), ct), ct);
        var all = ok.SelectMany(x => x.Value.Select(t => (x.Client, t))).ToList();
        var routes = all.GroupBy(x => $"{x.t["http_method"]} {Route((string)x.t["api_path"]!)}").Select(gr =>
        {
            var ms = gr.Select(x => (double)Convert.ToInt32(x.t["total_ms"])).OrderBy(v => v).ToList();
            return new
            {
                route = gr.Key, calls = ms.Count, avgMs = Math.Round(ms.Average()), p95Ms = Pct(ms, 0.95), maxMs = ms[^1],
                dbMs = Math.Round(gr.Average(x => Convert.ToInt32(x.t["database_ms"]))), queries = Math.Round(gr.Average(x => Convert.ToInt32(x.t["query_count"])), 1),
                errors = gr.Count(x => Convert.ToInt32(x.t["status_code"]) >= 500), slow = ms.Count(v => v >= SlowApiMs),
                clients = gr.Select(x => x.Client.Name).Distinct().Count(),
            };
        }).OrderByDescending(r => r.p95Ms).ToList();
        var slow = all.Where(x => Convert.ToInt32(x.t["total_ms"]) >= SlowApiMs).OrderByDescending(x => Convert.ToInt32(x.t["total_ms"])).Take(100)
            .Select(x => new
            {
                at = Dt(x.t["occurred_at_utc"]), client = x.Client.Name, clientId = x.Client.Id.ToString(CultureInfo.InvariantCulture), user = x.t["username"] as string,
                route = $"{x.t["http_method"]} {x.t["api_path"]}", ms = Convert.ToInt32(x.t["total_ms"]), dbMs = Convert.ToInt32(x.t["database_ms"]),
                status = Convert.ToInt32(x.t["status_code"]), error = x.t["error_message"] as string,
            });
        return Results.Ok(new { thresholdMs = SlowApiMs, routes, slow, unreachable = failed });
    }

    /* ------------------------------------------------------------------ SQL performance */

    private static async Task<IResult> SqlPerfAsync(ControlStore control, IDb db, int? hours, long? clientId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 30));
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, clientId, ct), (cl, conn) => Rows(db, conn, f => f.Query("audit.query_timing")
            .Where("occurred_at_utc", ">=", since).Select("command_text", "duration_ms", "rows_affected", "correlation_id", "occurred_at_utc")
            .OrderByDesc("occurred_at_utc").Limit(30000), ct), ct);
        var all = ok.SelectMany(x => x.Value.Select(q => (x.Client, q))).ToList();
        static double D(object? v) => Convert.ToDouble(v, CultureInfo.InvariantCulture);
        var statements = all.GroupBy(x => Sql((string)x.q["command_text"]!)).Select(gr =>
        {
            var ms = gr.Select(x => D(x.q["duration_ms"])).OrderBy(v => v).ToList();
            return new
            {
                sql = gr.Key, calls = ms.Count, avgMs = Math.Round(ms.Average(), 1), p95Ms = Pct(ms, 0.95), maxMs = Math.Round(ms[^1], 1),
                totalMs = Math.Round(ms.Sum()), rows = Math.Round(gr.Average(x => Convert.ToInt32(x.q["rows_affected"])), 1), slow = ms.Count(v => v >= SlowSqlMs),
                kind = Kind(gr.Key),
            };
        }).OrderByDescending(s => s.totalMs).Take(200).ToList();
        var slow = all.Where(x => D(x.q["duration_ms"]) >= SlowSqlMs).OrderByDescending(x => D(x.q["duration_ms"])).Take(100).Select(x => new
        {
            at = Dt(x.q["occurred_at_utc"]), client = x.Client.Name, clientId = x.Client.Id.ToString(CultureInfo.InvariantCulture),
            ms = Math.Round(D(x.q["duration_ms"]), 1), rows = Convert.ToInt32(x.q["rows_affected"]), sql = Sql((string)x.q["command_text"]!), requestId = x.q["correlation_id"] as string,
        });
        return Results.Ok(new { thresholdMs = SlowSqlMs, statements, slow, unreachable = failed });
    }

    /* ------------------------------------------------------------------ errors and front-end audit */

    private static async Task<IResult> ErrorsAsync(ControlStore control, string? source, long? clientId, bool? resolved, int? days, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var names = await Names(control, c, ct);
        var q = control.Query(c).Query("control.error_log").Where("last_at_utc", ">=", DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 365)))
            .OrderByDesc("last_at_utc").Limit(500);
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where("source", source);
        if (clientId is not null) q = q.Where("tenant_id", clientId);
        if (resolved is not null) q = q.Where("resolved", resolved.Value);
        var rows = (await q.GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        var counts = (await control.Query(c).Query("control.error_log").Where("resolved", false).Select("source").SelectRaw("COUNT(*) AS n").GroupBy("source")
            .GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToDictionary(r => (string)r["source"]!, r => Convert.ToInt32(r["n"]));
        return Results.Ok(new
        {
            open = counts,
            items = rows.Select(r => new
            {
                id = Convert.ToInt64(r["error_id"]).ToString(CultureInfo.InvariantCulture), source = (string)r["source"]!, app = (string)r["app"]!,
                clientId = r["tenant_id"] is null ? null : Convert.ToString(r["tenant_id"], CultureInfo.InvariantCulture),
                client = r["tenant_id"] is null ? "Ayn" : names.GetValueOrDefault(Convert.ToInt64(r["tenant_id"]), "#" + r["tenant_id"]),
                message = (string)r["message"]!, detail = r["detail"] as string, url = r["url"] as string, method = r["method"] as string,
                status = r["status_code"] is null ? (int?)null : Convert.ToInt32(r["status_code"]), user = r["user_name"] as string, userAgent = r["user_agent"] as string,
                ip = r["ip"] as string, occurrences = Convert.ToInt32(r["occurrences"]), firstAt = Dt(r["first_at_utc"]), lastAt = Dt(r["last_at_utc"]), resolved = (bool)r["resolved"]!,
            }),
        });
    }

    private static async Task<IResult> ResolveAsync(long id, ControlStore control, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        await using (var c = await control.OpenAsync(ct))
            await control.Query(c).Query("control.error_log").Where("error_id", id).UpdateAsync(new Dictionary<string, object?> { ["resolved"] = true }, cancellationToken: ct);
        var audit = await ConsoleEndpoints.Record(control, null, "error.resolve", "Marked error as resolved", null, $"error {id}", null, user, http, ct);
        return Results.Ok(new { ok = true, audit });
    }

    private static async Task<IResult> UiEventsAsync(ControlStore control, string? app, string? kind, int? take, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var names = await Names(control, c, ct);
        var q = control.Query(c).Query("control.ui_event").OrderByDesc("event_id").Limit(Math.Clamp(take ?? 300, 1, 2000));
        if (!string.IsNullOrWhiteSpace(app)) q = q.Where("app", app);
        if (!string.IsNullOrWhiteSpace(kind)) q = q.Where("kind", kind);
        var rows = (await q.GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>();
        return Results.Ok(rows.Select(r => new
        {
            id = Convert.ToInt64(r["event_id"]).ToString(CultureInfo.InvariantCulture), app = (string)r["app"]!, kind = (string)r["kind"]!, page = r["page"] as string,
            label = r["label"] as string, durationMs = r["duration_ms"] is null ? (int?)null : Convert.ToInt32(r["duration_ms"]), actor = r["actor"] as string, at = Dt(r["at_utc"]),
            client = r["tenant_id"] is null ? "Ayn" : names.GetValueOrDefault(Convert.ToInt64(r["tenant_id"]), "#" + r["tenant_id"]),
        }));
    }

    /* ------------------------------------------------------------------ what clients did */

    private static async Task<IResult> ClientActivityAsync(ControlStore control, IDb db, long? clientId, int? take, CancellationToken ct)
    {
        var limit = Math.Clamp(take ?? 60, 1, 500);
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, clientId, ct), (cl, conn) => Rows(db, conn, f => f.Query("audit.audit_entry")
            .WhereNotIn("action", new[] { "view", "read" })
            .Select("username", "role_id", "action", "module", "entity_type", "entity_id", "current_page", "result", "error_message", "ip_address", "occurred_at_utc")
            .OrderByDesc("audit_id").Limit(limit), ct), ct);
        var items = ok.SelectMany(x => x.Value.Select(a => new
        {
            at = Dt(a["occurred_at_utc"]), client = x.Client.Name, clientId = x.Client.Id.ToString(CultureInfo.InvariantCulture), user = a["username"] as string,
            role = a["role_id"] as string, action = (string)a["action"]!, module = (string)a["module"]!, entity = a["entity_type"] as string, entityId = a["entity_id"] as string,
            page = a["current_page"] as string, result = (string)a["result"]!, error = a["error_message"] as string, ip = a["ip_address"] as string,
        })).OrderByDescending(x => x.at).Take(limit);
        return Results.Ok(new { items, unreachable = failed });
    }

    private static async Task<IResult> LoginsAsync(ControlStore control, IDb db, long? clientId, int? days, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 90));
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, clientId, ct), (cl, conn) => LoginRows(db, conn, since, 2000, ct), ct);
        var items = ok.SelectMany(x => x.Value.Select(l => new
        {
            at = Dt(l["attempted_at_utc"]), client = x.Client.Name, clientId = x.Client.Id.ToString(CultureInfo.InvariantCulture), user = l["identifier"] as string,
            ok = (bool)l["succeeded"]!, ip = l["ip_address"] as string, device = Device(l["user_agent"] as string), reason = l["reason"] as string,
        })).OrderByDescending(x => x.at).Take(1000);
        return Results.Ok(new { items, unreachable = failed });
    }

    /// <summary>How clients use the product: sign-ins, active people, sales, busiest hours, most active users. Not API figures.</summary>
    private static async Task<IResult> ActivityAnalyticsAsync(ControlStore control, IDb db, int? days, long? clientId, CancellationToken ct)
    {
        var n = Math.Clamp(days ?? 30, 1, 365);
        var since = DateTime.UtcNow.Date.AddDays(-n + 1);
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, clientId, ct), async (cl, conn) => new
        {
            logins = await LoginRows(db, conn, since, 50000, ct),
            sales = await Rows(db, conn, f => f.Query("sales.sale").Where("sold_at_utc", ">=", since).Where("status", "<>", "voided").Select("total_h", "sold_at_utc", "cashier_name").Limit(100000), ct),
            voids = await Rows(db, conn, f => f.Query("sales.sale").Where("sold_at_utc", ">=", since).Where("status", "voided").SelectRaw("COUNT(*) AS n"), ct),
            actions = await Rows(db, conn, f => f.Query("audit.audit_entry").Where("occurred_at_utc", ">=", since).WhereNotIn("action", new[] { "view", "read" })
                .Select("username", "module").Limit(50000), ct),
        }, ct);

        var dates = Enumerable.Range(0, n).Select(i => since.AddDays(i)).ToList();
        var logins = ok.SelectMany(x => x.Value.logins.Select(l => (x.Client, l))).ToList();
        var sales = ok.SelectMany(x => x.Value.sales.Select(s => (x.Client, s))).ToList();
        var daily = dates.Select(d => new
        {
            date = d.ToString("yyyy-MM-dd"), label = d.ToString("dd MMM", CultureInfo.InvariantCulture),
            logins = logins.Count(x => (bool)x.l["succeeded"]! && Dt(x.l["attempted_at_utc"])?.Date == d),
            failedLogins = logins.Count(x => !(bool)x.l["succeeded"]! && Dt(x.l["attempted_at_utc"])?.Date == d),
            activeUsers = logins.Where(x => (bool)x.l["succeeded"]! && Dt(x.l["attempted_at_utc"])?.Date == d).Select(x => $"{x.Client.Id}:{x.l["identifier"]}").Distinct().Count(),
            sales = sales.Count(x => Dt(x.s["sold_at_utc"])?.Date == d),
            salesH = sales.Where(x => Dt(x.s["sold_at_utc"])?.Date == d).Sum(x => Convert.ToInt64(x.s["total_h"])),
        }).ToList();
        /* Saudi time for "busiest hours". */
        var ksa = TimeSpan.FromHours(3);
        var hoursOfDay = Enumerable.Range(0, 24).Select(h => new
        {
            hour = $"{h:00}:00", sales = sales.Count(x => Dt(x.s["sold_at_utc"]) is DateTime t && (t + ksa).Hour == h),
            logins = logins.Count(x => (bool)x.l["succeeded"]! && Dt(x.l["attempted_at_utc"]) is DateTime t && (t + ksa).Hour == h),
        }).ToList();
        var perClient = ok.Select(x => new
        {
            id = x.Client.Id.ToString(CultureInfo.InvariantCulture), name = x.Client.Name,
            logins = x.Value.logins.Count(l => (bool)l["succeeded"]!), failedLogins = x.Value.logins.Count(l => !(bool)l["succeeded"]!),
            users = x.Value.logins.Where(l => (bool)l["succeeded"]!).Select(l => l["identifier"]).Distinct().Count(),
            sales = x.Value.sales.Count, salesH = x.Value.sales.Sum(s => Convert.ToInt64(s["total_h"])),
            voids = x.Value.voids.Select(v => Convert.ToInt32(v["n"])).FirstOrDefault(), actions = x.Value.actions.Count,
            lastSaleAt = x.Value.sales.Select(s => Dt(s["sold_at_utc"])).Max(),
        }).OrderByDescending(x => x.sales).ToList();
        var topUsers = ok.SelectMany(x => x.Value.actions.Select(a => (x.Client, a))).GroupBy(x => (x.Client.Name, user: (string?)x.a["username"] ?? "—"))
            .Select(gr => new { client = gr.Key.Name, user = gr.Key.user, actions = gr.Count(), topModule = gr.GroupBy(x => (string)x.a["module"]!).MaxBy(m => m.Count())?.Key })
            .OrderByDescending(x => x.actions).Take(10).ToList();
        var modules = ok.SelectMany(x => x.Value.actions).GroupBy(a => (string)a["module"]!).Select(gr => new { module = gr.Key, actions = gr.Count() })
            .OrderByDescending(x => x.actions).Take(10).ToList();
        return Results.Ok(new
        {
            totals = new
            {
                logins = daily.Sum(d => d.logins), failedLogins = daily.Sum(d => d.failedLogins), sales = daily.Sum(d => d.sales), salesH = daily.Sum(d => d.salesH),
                activeUsers = logins.Where(x => (bool)x.l["succeeded"]!).Select(x => $"{x.Client.Id}:{x.l["identifier"]}").Distinct().Count(),
                activeClients = perClient.Count(c => c.logins > 0 || c.sales > 0), clients = perClient.Count,
            },
            daily, hoursOfDay, perClient, topUsers, modules, unreachable = failed,
        });
    }

    /// <summary>Every user of every client; the admin (created by Ayn) is flagged. Read-only: clients manage their own users.</summary>
    private static async Task<IResult> AllUsersAsync(ControlStore control, IDb db, CancellationToken ct)
    {
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, null, ct), async (cl, conn) => new
        {
            users = await Rows(db, conn, f => f.Query("core.app_user").OrderBy("user_id"), ct),
            roles = await Rows(db, conn, f => f.Query("core.role"), ct),
            logins = await LoginRows(db, conn, DateTime.UtcNow.AddDays(-90), 5000, ct),
        }, ct);
        var items = ok.SelectMany(x =>
        {
            var roleNames = x.Value.roles.ToDictionary(r => (string)r["role_id"]!, r => (r.TryGetValue("name_en", out var n) ? n as string : null) ?? (string)r["role_id"]!);
            var firstId = x.Value.users.Select(u => Convert.ToString(u["user_id"], CultureInfo.InvariantCulture)).FirstOrDefault();
            return x.Value.users.Select(u =>
            {
                var uid = Convert.ToString(u["user_id"], CultureInfo.InvariantCulture);
                var names = new[] { u["username"] as string, u["email"] as string, u["phone"] as string }.Where(v => !string.IsNullOrEmpty(v)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var last = x.Value.logins.Where(l => (bool)l["succeeded"]! && names.Contains((string)l["identifier"]!)).Select(l => Dt(l["attempted_at_utc"])).FirstOrDefault();
                var roleId = (string?)u["role_id"] ?? "";
                var isAdmin = roleId.Contains("owner", StringComparison.OrdinalIgnoreCase) || uid == firstId;
                return new
                {
                    id = $"{x.Client.Id}-{uid}", clientId = x.Client.Id.ToString(CultureInfo.InvariantCulture), clientName = x.Client.Name,
                    name = (u["name_en"] as string) ?? (u["name_ar"] as string), username = u["username"] as string, email = u["email"] as string, mobile = u["phone"] as string,
                    role = roleNames.GetValueOrDefault(roleId, roleId), isAdmin, createdBy = uid == firstId ? "Ayn" : "Client",
                    status = (u["status"] as string) ?? "active", lastLoginAt = last,
                };
            });
        }).OrderBy(u => u.clientName).ThenByDescending(u => u.isAdmin).ThenBy(u => u.name).ToList();
        return Results.Ok(new { items, unreachable = failed });
    }

    /* ------------------------------------------------------------------ create a database */

    private static async Task<IResult> CreateDatabaseAsync(DatabaseBody body, ControlStore control, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var (name, error) = await CreateClientDatabaseAsync(control, body.Name, ct);
        if (error is not null) return Results.Json(new { code = "db_create_failed", message = error, messageEn = error, messageAr = "تعذر إنشاء قاعدة البيانات." }, statusCode: 422);
        var login = control.ServerLogin();
        var audit = await ConsoleEndpoints.Record(control, null, "database.create", "Created database", null, name, null, user, http, ct);
        return Results.Ok(new { ok = true, database = name, host = login.Host, port = login.Port, user = login.User, audit });
    }

    /// <summary>A new empty database on the control database's server, named ayn_&lt;guid&gt; (or the name given), owned by its user.</summary>
    internal static async Task<(string Name, string? Error)> CreateClientDatabaseAsync(ControlStore control, string? wanted, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(wanted) ? $"ayn_{Guid.NewGuid():N}" : wanted.Trim().ToLowerInvariant();
        if (!DbName().IsMatch(name)) return (name, "Database names use 3-63 lowercase letters, digits or underscores, starting with a letter.");
        var login = control.ServerLogin();
        try
        {
            await using var c = await control.OpenAsync(ct);
            var exists = await c.ExecuteScalarAsync<int?>(control.Provider.Name == "postgres"
                ? "SELECT 1 FROM pg_database WHERE datname = @name" : "SELECT 1 FROM sys.databases WHERE name = @name", new { name });
            if (exists == 1) return (name, $"A database named {name} already exists.");
            await c.ExecuteAsync(control.Provider.Dialect.CreateDatabase(name, login.User));
            return (name, null);
        }
        catch (Exception ex)
        {
            return (name, $"Could not create database {name}: {ex.Message}. The database user needs permission to create databases (pgAdmin: ALTER ROLE {login.User} CREATEDB;).");
        }
    }

    /* ------------------------------------------------------------------ helpers */

    /// <summary>Sign-ins from each client's audit trail (LOGIN / LOGIN_FAILED), in the shape the screens read.</summary>
    internal static async Task<List<IDictionary<string, object?>>> LoginRows(IDb db, DbConnection conn, DateTime since, int take, CancellationToken ct)
    {
        var rows = await Rows(db, conn, f => f.Query("audit.audit_entry").WhereIn("action", new[] { "LOGIN", "LOGIN_FAILED" }).Where("occurred_at_utc", ">=", since)
            .Select("username", "action", "ip_address", "user_agent", "error_message", "occurred_at_utc").OrderByDesc("audit_id").Limit(take), ct);
        return rows.Select(r => (IDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["identifier"] = r["username"] ?? "(unknown)", ["succeeded"] = (string)r["action"]! == "LOGIN", ["ip_address"] = r["ip_address"],
            ["user_agent"] = r["user_agent"], ["reason"] = r["error_message"], ["attempted_at_utc"] = r["occurred_at_utc"],
        }).ToList();
    }

    private static async Task<Dictionary<long, string>> Names(ControlStore control, DbConnection c, CancellationToken ct) =>
        (await control.Query(c).Query("control.tenant").Select("tenant_id", "name").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>()
            .ToDictionary(r => Convert.ToInt64(r["tenant_id"]), r => (string)r["name"]!);

    internal static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)], 1);
    internal static DateTime? Dt(object? v) => v is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    internal static string Route(string path) => Ids().Replace(path.Split('?')[0], "/{id}");
    internal static string Sql(string text) { var s = Spaces().Replace(text, " ").Trim(); return s.Length > 400 ? s[..400] + "…" : s; }
    private static string Kind(string sql) => sql.TrimStart().Split(' ', 2)[0].ToUpperInvariant() switch { "SELECT" or "WITH" => "read", "INSERT" or "UPDATE" or "DELETE" or "MERGE" => "write", _ => "other" };
    private static string? Device(string? ua) => ua is null ? null : ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS"
        : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS") ? "Mac" : ua.Contains("Linux") ? "Linux" : "Other";

    [GeneratedRegex(@"/(\d+|[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}|[0-9a-fA-F]{24,})(?=/|$)")] private static partial Regex Ids();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^[a-z][a-z0-9_]{2,62}$")] private static partial Regex DbName();
}
