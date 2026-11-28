using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using PosApi.Auth;
using PosApi.Data;
using PosApi.Features.SuperAdmin;
using PosApi.Features.Zatca;
using PosApi.Tenancy;
using SqlKata.Execution;
using static PosApi.Observability.ObservabilityEndpoints;

namespace PosApi.Observability;

/// <summary>
/// Ayn, round 3: server load, today / yesterday / last-week trends, failures in depth, order and printing speed,
/// sessions, admin accounts, ZATCA per client, revenue and payments, and one search across everything.
/// Every figure is read from the control database or the clients' own databases.
/// </summary>
public static class AynEndpoints
{
    public sealed record UserStatusBody(bool Active, string? Reason);
    public sealed record AdminBody(string Username, string Name, string Email, string? Role);
    public sealed record PaymentBody(long ClientId, long AmountH, string? Method, string? Reference, string? Period, string? Note, string? PaidOn);

    public static void MapAynEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sa").WithTags("Ayn").RequireRateLimiting("standard")
            .RequireAuthorization(p => p.RequireRole(SuperAdminEndpoints.Role));

        g.MapGet("/server", ServerAsync);
        g.MapGet("/trends", TrendsAsync);
        g.MapGet("/clients/{id:long}/failures", FailuresAsync);
        g.MapGet("/orders/speed", (ControlStore c, IDb db, int? days, long? clientId, CancellationToken ct) => OrderSpeedAsync(c, db, days, clientId, ct));
        g.MapGet("/clients/{id:long}/orders/speed", (long id, ControlStore c, IDb db, int? days, CancellationToken ct) => OrderSpeedAsync(c, db, days, id, ct));
        g.MapGet("/sessions", SessionsAsync);
        g.MapPost("/clients/{id:long}/users/{userId:long}/status", UserStatusAsync);
        g.MapGet("/zatca/clients", ZatcaFleetAsync);
        g.MapGet("/clients/{id:long}/zatca", (long id, ControlStore c, IDb db, CancellationToken ct) => InClientAsync(c, db, id, ct, (_, _) => ZatcaSetupEndpoints.GetSetupAsync(db, ct)));
        g.MapPut("/clients/{id:long}/zatca", (long id, ZatcaSetupEndpoints.SaveBody body, ControlStore c, IDb db, ICurrentUser u, CancellationToken ct) =>
            InClientAsync(c, db, id, ct, (_, _) => ZatcaSetupEndpoints.SaveSetupAsync(body, db, u, ct)));
        g.MapPost("/clients/{id:long}/zatca/csr", (long id, ControlStore c, IDb db, IConfiguration cfg, CancellationToken ct) =>
            InClientAsync(c, db, id, ct, (_, _) => ZatcaSetupEndpoints.CsrAsync(db, cfg, ct)));
        g.MapPost("/clients/{id:long}/zatca/onboard", (long id, ZatcaSetupEndpoints.OtpBody body, ControlStore c, IDb db, IConfiguration cfg, CancellationToken ct) =>
            InClientAsync(c, db, id, ct, (_, _) => ZatcaSetupEndpoints.OnboardAsync(body, db, cfg, ct)));
        g.MapPost("/clients/{id:long}/zatca/production", (long id, ControlStore c, IDb db, IConfiguration cfg, CancellationToken ct) =>
            InClientAsync(c, db, id, ct, (_, _) => ZatcaSetupEndpoints.ProductionAsync(db, cfg, ct)));
        g.MapGet("/clients/{id:long}/zatca/invoices", ZatcaInvoicesAsync);
        g.MapGet("/revenue", RevenueAsync);
        g.MapGet("/revenue/payments", PaymentsAsync);
        g.MapPost("/revenue/payments", RecordPaymentAsync);
        g.MapGet("/platform/devices", DevicesAsync);
        g.MapGet("/support/issues", IssuesAsync);
        g.MapGet("/find", FindAsync);
        g.MapGet("/pulse", PulseAsync);
        /* Sliding sign-in: Ayn renews its token every few minutes while open, so nobody is signed out mid-work. */
        g.MapPost("/auth/renew", (ICurrentUser user, IJwtTokenService tokens, IConfiguration config) =>
        {
            var pair = tokens.Issue(user.UserId, user.Username, SuperAdminEndpoints.Role, ["sa.all"], [], SuperAdminEndpoints.AdminTokenLife(config));
            return Results.Ok(new { accessToken = pair.AccessToken, expiresAtUtc = pair.AccessExpiresAtUtc });
        });
        g.MapGet("/admins", AdminsAsync);
        g.MapPost("/admins", CreateAdminAsync);
        g.MapPost("/admins/{adminId:long}/status", AdminStatusAsync);
    }

    private static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static long L(object? v) => v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    private static IResult Fail(int status, string code, string en) => Results.Json(new { code, message = en, messageEn = en }, statusCode: status);

    /// <summary>Runs one action inside one client's database (TenantContext set for its duration).</summary>
    private static async Task<IResult> InClientAsync(ControlStore control, IDb db, long id, CancellationToken ct, Func<ClientRef, DbConnection?, Task<IResult>> work)
    {
        var client = (await ClientsAsync(control, id, ct)).FirstOrDefault();
        if (client is null) return Fail(404, "not_found", "That client does not exist or is not set up yet.");
        TenantContext.Current = client.Tenant;
        try { return await work(client, null); }
        catch (Exception ex) { return Fail(502, "client_unreachable", $"Could not work in this client's database: {ex.Message}"); }
        finally { TenantContext.Current = null; }
    }

    /* ══════════════════════════════════════ server load ══ */

    private static async Task<IResult> ServerAsync(ServerSampler sampler, ControlStore control, CancellationToken ct)
    {
        var now = await sampler.ReadAsync(ct);
        await using var c = await control.OpenAsync(ct);
        var since = DateTime.UtcNow.Date.AddDays(-8);
        var samples = (await control.Query(c).Query("control.server_sample").Where("at_utc", ">=", since).OrderBy("at_utc").GetAsync(cancellationToken: ct))
            .Cast<IDictionary<string, object?>>().ToList();
        double D(object? v) => Convert.ToDouble(v, CultureInfo.InvariantCulture);
        object Day(DateTime day) => Enumerable.Range(0, 24).Select(h =>
        {
            var inHour = samples.Where(s => Dt(s["at_utc"]) is DateTime t && t >= day.AddHours(h) && t < day.AddHours(h + 1)).ToList();
            return new
            {
                hour = $"{h:00}:00",
                cpu = inHour.Count == 0 ? (double?)null : Math.Round(inHour.Average(s => D(s["cpu_pct"])), 1),
                memory = inHour.Count == 0 ? (double?)null : Math.Round(inHour.Average(s => D(s["memory_pct"])), 1),
                requests = inHour.Sum(s => Convert.ToInt32(s["requests"])),
                errors = inHour.Sum(s => Convert.ToInt32(s["errors"])),
                connections = inHour.Count == 0 ? (int?)null : (int)Math.Round(inHour.Average(s => Convert.ToInt32(s["db_connections"]))),
            };
        }).ToList();
        var today = DateTime.UtcNow.Date;
        object Summary(DateTime day)
        {
            var list = samples.Where(s => Dt(s["at_utc"]) is DateTime t && t >= day && t < day.AddDays(1)).ToList();
            return new
            {
                cpuAvg = list.Count == 0 ? (double?)null : Math.Round(list.Average(s => D(s["cpu_pct"])), 1),
                cpuMax = list.Count == 0 ? (double?)null : Math.Round(list.Max(s => D(s["cpu_pct"])), 1),
                memoryAvg = list.Count == 0 ? (double?)null : Math.Round(list.Average(s => D(s["memory_pct"])), 1),
                requests = list.Sum(s => Convert.ToInt32(s["requests"])), errors = list.Sum(s => Convert.ToInt32(s["errors"])),
                samples = list.Count,
            };
        }
        return Results.Ok(new
        {
            now = new
            {
                cpuPct = now.CpuPct, memoryPct = now.MemoryPct, memoryUsedBytes = now.MemoryUsedBytes, memoryTotalBytes = now.MemoryTotalBytes,
                processMb = now.ProcessMb, diskFreePct = now.DiskFreePct, diskFreeBytes = now.DiskFreeBytes, diskTotalBytes = now.DiskTotalBytes,
                dbConnections = now.DbConnections, threads = now.Threads, threadPoolBusy = now.ThreadPoolBusy, cores = Environment.ProcessorCount,
                machine = Environment.MachineName, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, at = now.AtUtc,
            },
            hourly = new { today = Day(today), yesterday = Day(today.AddDays(-1)), lastWeek = Day(today.AddDays(-7)) },
            summary = new { today = Summary(today), yesterday = Summary(today.AddDays(-1)), lastWeek = Summary(today.AddDays(-7)) },
            sampledSince = samples.Count == 0 ? (DateTime?)null : Dt(samples[0]["at_utc"]),
        });
    }

    /* ══════════════════════════════════════ trends: today vs yesterday vs last week ══ */

    private static async Task<IResult> TrendsAsync(ControlStore control, IDb db, long? clientId, int? days, CancellationToken ct)
    {
        var n = Math.Clamp(days ?? 30, 7, 90);
        var since = DateTime.UtcNow.Date.AddDays(-Math.Max(n, 8));
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, clientId, ct), async (cl, conn) => new
        {
            sales = await Rows(db, conn, f => f.Query("sales.sale").Where("sold_at_utc", ">=", since).Select("sold_at_utc", "status", "total_h", "voided_at_utc").Limit(200000), ct),
            logins = await LoginRows(db, conn, since, 100000, ct),
            fails = await Rows(db, conn, f => f.Query("audit.request_timing").Where("occurred_at_utc", ">=", since).Where("status_code", ">=", 400)
                .Select("status_code", "occurred_at_utc").Limit(100000), ct),
        }, ct);
        await using var c = await control.OpenAsync(ct);
        var errorRows = (await control.Query(c).Query("control.error_log").Where("last_at_utc", ">=", since).Select("tenant_id", "first_at_utc", "occurrences").GetAsync(cancellationToken: ct))
            .Cast<IDictionary<string, object?>>().Where(e => clientId is null || L(e["tenant_id"]) == clientId).ToList();

        var events = new Dictionary<string, List<(DateTime At, long Value)>>
        {
            ["orders"] = ok.SelectMany(x => x.Value.sales.Where(s => (string)s["status"]! != "voided").Select(s => (Dt(s["sold_at_utc"])!.Value, 1L))).ToList(),
            ["sales"] = ok.SelectMany(x => x.Value.sales.Where(s => (string)s["status"]! != "voided").Select(s => (Dt(s["sold_at_utc"])!.Value, L(s["total_h"])))).ToList(),
            ["voids"] = ok.SelectMany(x => x.Value.sales.Where(s => (string)s["status"]! == "voided").Select(s => ((Dt(s["voided_at_utc"]) ?? Dt(s["sold_at_utc"]))!.Value, 1L))).ToList(),
            ["logins"] = ok.SelectMany(x => x.Value.logins.Where(l => (bool)l["succeeded"]!).Select(l => (Dt(l["attempted_at_utc"])!.Value, 1L))).ToList(),
            ["failedLogins"] = ok.SelectMany(x => x.Value.logins.Where(l => !(bool)l["succeeded"]!).Select(l => (Dt(l["attempted_at_utc"])!.Value, 1L))).ToList(),
            ["apiFailures"] = ok.SelectMany(x => x.Value.fails.Select(f => (Dt(f["occurred_at_utc"])!.Value, 1L))).ToList(),
            ["errors"] = errorRows.Select(e => (Dt(e["first_at_utc"])!.Value, (long)Convert.ToInt32(e["occurrences"]))).ToList(),
        };
        var today = DateTime.UtcNow.Date;
        object Metric(List<(DateTime At, long Value)> list)
        {
            long[] Hours(DateTime day) => Enumerable.Range(0, 24).Select(h => list.Where(x => x.At >= day.AddHours(h) && x.At < day.AddHours(h + 1)).Sum(x => x.Value)).ToArray();
            var nowHour = DateTime.UtcNow.Hour;
            long Upto(DateTime day) => list.Where(x => x.At >= day && x.At < day.AddHours(nowHour + 1)).Sum(x => x.Value);
            return new
            {
                today = list.Where(x => x.At >= today).Sum(x => x.Value),
                yesterday = list.Where(x => x.At >= today.AddDays(-1) && x.At < today).Sum(x => x.Value),
                lastWeek = list.Where(x => x.At >= today.AddDays(-7) && x.At < today.AddDays(-6)).Sum(x => x.Value),
                /* Same time of day, so a morning is not compared with a whole day. */
                yesterdaySoFar = Upto(today.AddDays(-1)), lastWeekSoFar = Upto(today.AddDays(-7)),
                thisWeek = list.Where(x => x.At >= today.AddDays(-6)).Sum(x => x.Value),
                previousWeek = list.Where(x => x.At >= today.AddDays(-13) && x.At < today.AddDays(-6)).Sum(x => x.Value),
                hourly = new { today = Hours(today), yesterday = Hours(today.AddDays(-1)), lastWeek = Hours(today.AddDays(-7)) },
                daily = Enumerable.Range(0, n).Select(i => today.AddDays(-(n - 1 - i))).Select(d => new
                {
                    date = d.ToString("yyyy-MM-dd"), label = d.ToString("dd MMM", CultureInfo.InvariantCulture),
                    value = list.Where(x => x.At >= d && x.At < d.AddDays(1)).Sum(x => x.Value),
                }).ToList(),
            };
        }
        return Results.Ok(new
        {
            metrics = events.ToDictionary(kv => kv.Key, kv => Metric(kv.Value)),
            perClient = ok.Select(x => new
            {
                id = Id(x.Client.Id), name = x.Client.Name,
                ordersToday = x.Value.sales.Count(s => (string)s["status"]! != "voided" && Dt(s["sold_at_utc"]) >= today),
                ordersYesterday = x.Value.sales.Count(s => (string)s["status"]! != "voided" && Dt(s["sold_at_utc"]) >= today.AddDays(-1) && Dt(s["sold_at_utc"]) < today),
                voidsToday = x.Value.sales.Count(s => (string)s["status"]! == "voided" && (Dt(s["voided_at_utc"]) ?? Dt(s["sold_at_utc"])) >= today),
                loginsToday = x.Value.logins.Count(l => (bool)l["succeeded"]! && Dt(l["attempted_at_utc"]) >= today),
                failuresToday = x.Value.fails.Count(f => Dt(f["occurred_at_utc"]) >= today),
            }).OrderByDescending(x => x.ordersToday).ToList(),
            unreachable = failed,
        });
    }

    /* ══════════════════════════════════════ failures in depth ══ */

    private static async Task<IResult> FailuresAsync(long id, ControlStore control, IDb db, int? days, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 90));
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, id, ct), async (cl, conn) =>
        {
            var fails = await Rows(db, conn, f => f.Query("audit.request_timing").Where("occurred_at_utc", ">=", since).Where("status_code", ">=", 400)
                .OrderByDesc("occurred_at_utc").Limit(20000), ct);
            var entries = await Rows(db, conn, f => f.Query("audit.audit_entry").Where("occurred_at_utc", ">=", since).Where("result", "<>", "success")
                .Select("correlation_id", "action", "module", "entity_type", "entity_id", "error_message", "details").Limit(20000), ct);
            var voids = await Rows(db, conn, f => f.Query("sales.sale").Where("status", "voided").Where("sold_at_utc", ">=", since)
                .Select("invoice_no", "order_no", "total_h", "cashier_name", "void_reason", "voided_by", "voided_at_utc", "sold_at_utc").OrderByDesc("voided_at_utc").Limit(2000), ct);
            /* The queries of the most recent occurrence of each failure, to show what ran. */
            var latest = fails.GroupBy(r => Key(r)).Select(gr => (string)gr.First()["correlation_id"]!).ToArray();
            var queries = latest.Length == 0 ? [] : await Rows(db, conn, f => f.Query("audit.query_timing").WhereIn("correlation_id", latest)
                .Select("correlation_id", "seq", "command_text", "duration_ms", "rows_affected").OrderBy("correlation_id").OrderBy("seq"), ct);
            return new { fails, entries, voids, queries };
        }, ct);
        if (ok.Count == 0) return Results.Ok(new { groups = Array.Empty<object>(), voids = Array.Empty<object>(), unreachable = failed });
        var v = ok[0].Value;
        var entryByCorrelation = v.entries.Where(e => e["correlation_id"] is string).GroupBy(e => (string)e["correlation_id"]!).ToDictionary(g => g.Key, g => g.First());
        var queriesByCorrelation = v.queries.GroupBy(q => (string)q["correlation_id"]!).ToDictionary(g => g.Key, g => g.ToList());
        var groups = v.fails.GroupBy(Key).Select(gr =>
        {
            var list = gr.ToList();
            var last = list[0];
            var corr = (string)last["correlation_id"]!;
            var entry = entryByCorrelation.GetValueOrDefault(corr);
            var times = list.Select(r => Dt(r["occurred_at_utc"])!.Value).OrderBy(t => t).ToList();
            var spanHours = Math.Max(1, (times[^1] - times[0]).TotalHours);
            var pages = list.Select(r => r["current_page"] as string).Where(p => !string.IsNullOrEmpty(p)).GroupBy(p => p!).Select(p => new { page = p.Key, times = p.Count() }).OrderByDescending(p => p.times).ToList();
            var status = Convert.ToInt32(last["status_code"]);
            return new
            {
                key = gr.Key, method = (string)last["http_method"]!, api = Route((string)last["api_path"]!), status,
                kind = status >= 500 ? "server" : status == 401 || status == 403 ? "refused" : status == 404 ? "not_found" : status == 409 ? "conflict" : "invalid",
                why = (last["error_message"] as string) ?? (entry?["error_message"] as string) ?? Explain(status),
                occurrences = list.Count, users = list.Select(r => r["username"] as string).Where(u => u is not null).Distinct().Count(),
                pagesAffected = pages.Count, pages,
                firstAt = times[0], lastAt = times[^1], perDay = Math.Round(list.Count / Math.Max(1, (DateTime.UtcNow - since).TotalDays), 1),
                perHourWhenHappening = Math.Round(list.Count / spanHours, 1),
                avgMs = Math.Round(list.Average(r => Convert.ToInt32(r["total_ms"]))), maxMs = list.Max(r => Convert.ToInt32(r["total_ms"])),
                avgDbMs = Math.Round(list.Average(r => Convert.ToInt32(r["database_ms"]))),
                byHour = Enumerable.Range(0, 24).Select(h => list.Count(r => Dt(r["occurred_at_utc"])!.Value.AddHours(3).Hour == h)).ToArray(),
                scenario = new
                {
                    user = last["username"] as string, page = last["current_page"] as string, previousPage = last["previous_page"] as string,
                    action = entry?["action"] as string, module = entry?["module"] as string,
                    on = entry is null ? null : string.Join(" #", new[] { entry["entity_type"] as string, entry["entity_id"] as string }.Where(x => !string.IsNullOrEmpty(x))),
                    detail = entry?["details"] as string, at = Dt(last["occurred_at_utc"]), requestId = corr, path = last["api_path"] as string,
                },
                queries = queriesByCorrelation.GetValueOrDefault(corr, []).Select(q => new
                {
                    seq = Convert.ToInt32(q["seq"]), sql = Sql((string)q["command_text"]!), ms = Math.Round(Convert.ToDouble(q["duration_ms"], CultureInfo.InvariantCulture), 1),
                    rows = Convert.ToInt32(q["rows_affected"]),
                }),
                recent = list.Take(15).Select(r => new
                {
                    at = Dt(r["occurred_at_utc"]), user = r["username"] as string, page = r["current_page"] as string, ms = Convert.ToInt32(r["total_ms"]),
                    error = r["error_message"] as string, requestId = r["correlation_id"] as string,
                }),
            };
        }).OrderByDescending(gr => gr.occurrences).ToList();
        var voids = v.voids.Select(s => new
        {
            invoiceNo = L(s["invoice_no"]), orderNo = s["order_no"] is null ? (int?)null : Convert.ToInt32(s["order_no"]), totalH = L(s["total_h"]),
            cashier = s["cashier_name"] as string, reason = (s["void_reason"] as string) ?? "No reason given", by = s["voided_by"] as string,
            soldAt = Dt(s["sold_at_utc"]), voidedAt = Dt(s["voided_at_utc"]),
            minutesAfterSale = Dt(s["voided_at_utc"]) is DateTime va && Dt(s["sold_at_utc"]) is DateTime sa ? Math.Round((va - sa).TotalMinutes, 1) : (double?)null,
        }).ToList();
        return Results.Ok(new
        {
            groups, voids,
            voidReasons = voids.GroupBy(x => x.reason).Select(gr => new { reason = gr.Key, times = gr.Count(), totalH = gr.Sum(x => x.totalH) }).OrderByDescending(x => x.times),
            voidsBy = voids.GroupBy(x => x.by ?? x.cashier ?? "—").Select(gr => new { who = gr.Key, times = gr.Count(), totalH = gr.Sum(x => x.totalH) }).OrderByDescending(x => x.times),
            totals = new { failures = groups.Sum(x => x.occurrences), distinct = groups.Count, voids = voids.Count, voidsH = voids.Sum(x => x.totalH) },
            unreachable = failed,
        });

        static string Key(IDictionary<string, object?> r) => $"{r["http_method"]} {Route((string)r["api_path"]!)} {r["status_code"]} {((r["error_message"] as string) ?? "").Split('\n')[0]}";
        static string Explain(int s) => s switch
        {
            400 => "The request was not valid (a required value was missing or wrong).",
            401 => "Not signed in, or the sign-in expired.",
            403 => "Signed in, but this user's role does not allow it.",
            404 => "What was asked for does not exist (deleted, or a wrong number).",
            409 => "A conflict: the record changed or already exists.",
            422 => "The values were refused by a business rule.",
            429 => "Too many requests in a short time (rate limit).",
            >= 500 => "The server failed while handling it — see the Errors page for the technical detail.",
            _ => $"Answered {s}.",
        };
    }

    /* ══════════════════════════════════════ order and printing speed ══ */

    private static async Task<IResult> OrderSpeedAsync(ControlStore control, IDb db, int? days, long? clientId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 1, 1, 30));
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, clientId, ct), async (cl, conn) => new
        {
            sales = await Rows(db, conn, f => f.Query("sales.sale").Where("sold_at_utc", ">=", since).Where("status", "<>", "voided")
                .Select("invoice_no", "order_no", "order_type", "cashier_name", "total_h", "item_count", "sold_at_utc").OrderBy("sold_at_utc").Limit(20000), ct),
            events = await Rows(db, conn, f => f.Query("sales.order_event").Where("at_utc", ">=", since.AddMinutes(-5)).Select("invoice_no", "kind", "at_utc", "actor").Limit(50000), ct),
            tickets = await Rows(db, conn, f => f.Query("sales.kitchen_ticket").Where("created_at_utc", ">=", since.AddMinutes(-5)).Select("invoice_no", "document_json", "created_at_utc").Limit(20000), ct),
        }, ct);
        static DateTime? Parse(JsonNode? n, string key) => n?[key]?.ToString() is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        static double? Sec(DateTime? a, DateTime? b) => a is null || b is null ? null : Math.Round((b.Value - a.Value).TotalSeconds, 1);
        var orders = ok.SelectMany(x =>
        {
            var ev = x.Value.events.GroupBy(e => L(e["invoice_no"])).ToDictionary(gr => gr.Key, gr => gr.ToList());
            var tk = x.Value.tickets.GroupBy(t => L(t["invoice_no"])).ToDictionary(gr => gr.Key, gr => gr.First());
            DateTime? prev = null;
            return x.Value.sales.Select(s =>
            {
                var inv = L(s["invoice_no"]);
                var placed = Dt(s["sold_at_utc"])!.Value;
                var e = ev.GetValueOrDefault(inv, []);
                DateTime? First(string kind) => e.Where(z => (string)z["kind"]! == kind).Select(z => Dt(z["at_utc"])).Min();
                JsonNode? doc = null;
                if (tk.TryGetValue(inv, out var t) && t["document_json"] is string j) { try { doc = JsonNode.Parse(j); } catch (JsonException) { } }
                var kitchenSent = Parse(doc, "createdAt") ?? (tk.TryGetValue(inv, out var t2) ? Dt(t2["created_at_utc"]) : null);
                var receipt = First("print_receipt"); var kitchenPrint = First("print_kitchen");
                var started = Parse(doc, "startedAt"); var ready = Parse(doc, "readyAt"); var collected = Parse(doc, "collectedAt");
                var gap = Sec(prev, placed); prev = placed;
                return new
                {
                    clientId = Id(x.Client.Id), client = x.Client.Name, invoiceNo = inv, orderNo = s["order_no"] is null ? (int?)null : Convert.ToInt32(s["order_no"]),
                    type = s["order_type"] as string, cashier = s["cashier_name"] as string, totalH = L(s["total_h"]),
                    items = Convert.ToDouble(s["item_count"] ?? 0, CultureInfo.InvariantCulture),
                    placedAt = placed, receiptPrintedAt = receipt, kitchenPrintedAt = kitchenPrint, kitchenSentAt = kitchenSent, startedAt = started, readyAt = ready, collectedAt = collected,
                    secondsSincePrevious = gap,
                    toReceiptPrint = Sec(placed, receipt), toKitchenPrint = Sec(placed, kitchenPrint), toKitchen = Sec(placed, kitchenSent),
                    waitBeforeStart = Sec(kitchenSent, started), prepare = Sec(started, ready), readyToCollected = Sec(ready, collected), total = Sec(placed, collected ?? ready),
                    prints = e.Count(z => ((string)z["kind"]!).StartsWith("print_", StringComparison.Ordinal)),
                };
            });
        }).OrderByDescending(o => o.placedAt).ToList();
        static object Stat(IEnumerable<double?> values)
        {
            var v = values.Where(x => x is >= 0).Select(x => x!.Value).OrderBy(x => x).ToList();
            return new { count = v.Count, avg = v.Count == 0 ? (double?)null : Math.Round(v.Average(), 1), median = v.Count == 0 ? (double?)null : v[v.Count / 2], p90 = v.Count == 0 ? (double?)null : Pct(v, 0.9), max = v.Count == 0 ? (double?)null : v[^1] };
        }
        var hours = Math.Max(1, (DateTime.UtcNow - since).TotalHours);
        return Results.Ok(new
        {
            orders = orders.Count, perHour = Math.Round(orders.Count / hours, 1),
            busiestHour = orders.GroupBy(o => o.placedAt.AddHours(3).ToString("yyyy-MM-dd HH:00")).Select(gr => new { hour = gr.Key, orders = gr.Count() }).OrderByDescending(x => x.orders).FirstOrDefault(),
            stages = new
            {
                betweenOrders = Stat(orders.Select(o => o.secondsSincePrevious)),
                toReceiptPrint = Stat(orders.Select(o => o.toReceiptPrint)), toKitchenPrint = Stat(orders.Select(o => o.toKitchenPrint)),
                toKitchen = Stat(orders.Select(o => o.toKitchen)), waitBeforeStart = Stat(orders.Select(o => o.waitBeforeStart)),
                prepare = Stat(orders.Select(o => o.prepare)), readyToCollected = Stat(orders.Select(o => o.readyToCollected)), total = Stat(orders.Select(o => o.total)),
            },
            perHourSeries = Enumerable.Range(0, 24).Select(h => new { hour = $"{h:00}:00", value = orders.Count(o => o.placedAt.AddHours(3).Hour == h) }),
            notPrinted = orders.Count(o => o.prints == 0),
            list = orders.Take(200),
            unreachable = failed,
        });
    }

    /* ══════════════════════════════════════ sessions: open and close ══ */

    private static async Task<IResult> SessionsAsync(ControlStore control, IDb db, long? clientId, int? days, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 14, 1, 180));
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, clientId, ct), async (cl, conn) => new
        {
            sessions = await Rows(db, conn, f => f.Query("sales.restaurant_session").Where(w => w.Where("opened_at_utc", ">=", since).OrWhereNull("closed_at_utc"))
                .OrderByDesc("opened_at_utc").Limit(1000), ct),
            sales = await Rows(db, conn, f => f.Query("sales.sale").Where("sold_at_utc", ">=", since).Select("session_id", "status", "total_h").Limit(200000), ct),
            branches = await Rows(db, conn, f => f.Query("core.branch").Select("branch_id", "name_en"), ct),
        }, ct);
        var items = ok.SelectMany(x =>
        {
            var bySession = x.Value.sales.Where(s => s["session_id"] is not null).GroupBy(s => L(s["session_id"])).ToDictionary(gr => gr.Key, gr => gr.ToList());
            var branch = x.Value.branches.ToDictionary(b => L(b["branch_id"]), b => b["name_en"] as string);
            return x.Value.sessions.Select(s =>
            {
                var sid = L(s["session_id"]);
                var sales = bySession.GetValueOrDefault(sid, []);
                var opened = Dt(s["opened_at_utc"]); var closed = Dt(s["closed_at_utc"]);
                return new
                {
                    clientId = Id(x.Client.Id), client = x.Client.Name, sessionNo = L(s["session_no"]), branch = branch.GetValueOrDefault(L(s["branch_id"])),
                    register = s["register_id"] as string, businessDate = s["business_date"] is DateTime bd ? bd.ToString("yyyy-MM-dd") : null,
                    status = (string)s["status"]!, openedAt = opened, openedBy = s["opened_by"] as string, closedAt = closed, closedBy = s["closed_by"] as string,
                    minutesOpen = opened is null ? (double?)null : Math.Round(((closed ?? DateTime.UtcNow) - opened.Value).TotalMinutes),
                    openingH = L(s["opening_balance_h"]), expectedH = s["expected_cash_h"] is null ? (long?)null : L(s["expected_cash_h"]),
                    countedH = s["actual_closing_h"] is null ? (long?)null : L(s["actual_closing_h"]), discrepancyH = s["discrepancy_h"] is null ? (long?)null : L(s["discrepancy_h"]),
                    discrepancyReason = s["discrepancy_reason"] as string, reopened = Convert.ToInt32(s["reopen_count"] ?? 0),
                    orders = sales.Count(z => (string)z["status"]! != "voided"), voids = sales.Count(z => (string)z["status"]! == "voided"),
                    salesH = sales.Where(z => (string)z["status"]! != "voided").Sum(z => L(z["total_h"])),
                };
            });
        }).OrderByDescending(s => s.openedAt).ToList();
        return Results.Ok(new
        {
            items,
            openNow = items.Count(s => s.closedAt is null),
            avgMinutesOpen = items.Where(s => s.closedAt is not null && s.minutesOpen is not null).Select(s => s.minutesOpen!.Value).DefaultIfEmpty(0).Average(),
            withDiscrepancy = items.Count(s => s.discrepancyH is not null and not 0),
            unreachable = failed,
        });
    }

    /* ══════════════════════════════════════ deactivate / activate a client's user (e.g. the admin) ══ */

    private static async Task<IResult> UserStatusAsync(long id, long userId, UserStatusBody body, ControlStore control, IDb db, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        if (!body.Active && string.IsNullOrWhiteSpace(body.Reason)) return Fail(422, "reason_required", "Give a reason for deactivating this user.");
        string? name = null; string? before = null;
        var result = await InClientAsync(control, db, id, ct, async (_, _) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var q = db.Query(conn);
            var row = (await q.Query("core.app_user").Where("user_id", userId).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();
            if (row is null) return Fail(404, "not_found", "That user does not exist at this client.");
            name = (row["name_en"] as string) ?? (row["username"] as string);
            before = row["status"] as string;
            await q.Query("core.app_user").Where("user_id", userId).UpdateAsync(new Dictionary<string, object?>
            {
                ["status"] = body.Active ? "active" : "suspended", ["updated_at_utc"] = DateTime.UtcNow, ["updated_by"] = $"Ayn: {user.Username}",
            }, cancellationToken: ct);
            /* Signed out everywhere: refresh tokens revoked, so the next token renewal is refused. */
            if (!body.Active)
                await conn.ExecuteAsync("UPDATE core.refresh_token SET revoked_at_utc = @Now WHERE user_id = @Id AND revoked_at_utc IS NULL", new { Now = DateTime.UtcNow, Id = userId });
            return Results.Ok();
        });
        if (result is not Microsoft.AspNetCore.Http.HttpResults.Ok) return result;
        var audit = await ConsoleEndpoints.Record(control, id, body.Active ? "user.activate" : "user.deactivate",
            body.Active ? $"Activated user {name}" : $"Deactivated user {name}", before, body.Active ? "active" : "suspended", body.Reason, user, http, ct);
        return Results.Ok(new { ok = true, audit });
    }

    /* ══════════════════════════════════════ ZATCA phase 2 per client ══ */

    private static async Task<IResult> ZatcaFleetAsync(ControlStore control, IDb db, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, null, ct), async (cl, conn) => new
        {
            setup = (await Rows(db, conn, f => f.Query("core.zatca_setup").Select("environment", "status", "details_json", "last_error", "updated_at_utc", "updated_by",
                "compliance_json_enc", "production_json_enc"), ct)).FirstOrDefault(),
            counts = (await Rows(db, conn, f => f.Query("sales.sale").Where("sold_at_utc", ">=", today.AddDays(-30))
                .SelectRaw("COUNT(*) AS total, COUNT(zatca_qr) AS with_qr, COUNT(zatca_hash) AS signed, MAX(zatca_icv) AS last_icv, MAX(sold_at_utc) AS last_at"), ct)).First(),
            chain = await Rows(db, conn, f => f.Query("sales.zatca_chain"), ct),
        }, ct);
        var items = ok.Select(x =>
        {
            var s = x.Value.setup;
            var details = s?["details_json"] is string j ? JsonNode.Parse(j) : null;
            var status = (s?["status"] as string) ?? "not_started";
            var total = L(x.Value.counts["total"]); var withQr = L(x.Value.counts["with_qr"]);
            return new
            {
                clientId = Id(x.Client.Id), client = x.Client.Name, environment = s?["environment"] as string, status,
                step = status switch { "not_started" => 0, "draft" => 2, "csr_ready" => 3, "compliance" => 4, "live" => 5, _ => 1 },
                vatNumber = details?["vatNumber"]?.ToString(), sellerName = details?["sellerNameEn"]?.ToString() ?? details?["sellerNameAr"]?.ToString(),
                device = details?["deviceName"]?.ToString(), hasCompliance = s?["compliance_json_enc"] is string, hasProduction = s?["production_json_enc"] is string,
                lastError = s?["last_error"] as string, updatedAt = s is null ? null : Dt(s["updated_at_utc"]), updatedBy = s?["updated_by"] as string,
                invoices30d = total, withQr30d = withQr, signed30d = L(x.Value.counts["signed"]), missingQr30d = total - withQr,
                lastIcv = x.Value.counts["last_icv"] is null ? (long?)null : L(x.Value.counts["last_icv"]), lastInvoiceAt = Dt(x.Value.counts["last_at"]),
                chains = x.Value.chain.Select(c => new { device = c["egs_unit_id"] as string, icv = L(c["icv"]), lastHash = c["last_hash"] as string, at = Dt(c["updated_at_utc"]) }),
            };
        }).OrderBy(x => x.step).ThenBy(x => x.client).ToList();
        return Results.Ok(new
        {
            items,
            totals = new
            {
                clients = items.Count, live = items.Count(i => i.status == "live"), inProgress = items.Count(i => i.step is > 0 and < 5),
                notStarted = items.Count(i => i.step == 0), withErrors = items.Count(i => i.lastError is not null),
                invoices30d = items.Sum(i => i.invoices30d), missingQr30d = items.Sum(i => i.missingQr30d),
            },
            unreachable = failed,
        });
    }

    private static async Task<IResult> ZatcaInvoicesAsync(long id, ControlStore control, IDb db, int? take, string? q, CancellationToken ct)
    {
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, id, ct), (cl, conn) => Rows(db, conn, f =>
        {
            var query = f.Query("sales.sale").Select("invoice_no", "status", "total_h", "tax_h", "sold_at_utc", "zatca_uuid", "zatca_icv", "zatca_pih", "zatca_hash", "zatca_qr", "void_reason")
                .OrderByDesc("invoice_no").Limit(Math.Clamp(take ?? 100, 1, 500));
            if (long.TryParse(q, out var n)) query = query.Where("invoice_no", n);
            return query;
        }, ct), ct);
        var items = ok.SelectMany(x => x.Value.Select(s => new
        {
            invoiceNo = L(s["invoice_no"]), status = (string)s["status"]!, totalH = L(s["total_h"]), vatH = L(s["tax_h"]), at = Dt(s["sold_at_utc"]),
            uuid = s["zatca_uuid"] as string, icv = s["zatca_icv"] is null ? (long?)null : L(s["zatca_icv"]), previousHash = s["zatca_pih"] as string,
            hash = s["zatca_hash"] as string, qr = s["zatca_qr"] as string, qrFields = DecodeQr(s["zatca_qr"] as string),
            state = s["zatca_qr"] is null ? "missing_qr" : s["zatca_hash"] is null ? "qr_only" : "signed",
        }));
        return Results.Ok(new { items, unreachable = failed });
    }

    /// <summary>The ZATCA QR is TLV in base64: 1 seller, 2 VAT no., 3 time, 4 total, 5 VAT, 6 hash, 7 signature, 8 public key, 9 stamp.</summary>
    private static object? DecodeQr(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            var tags = new Dictionary<int, string>();
            for (var i = 0; i + 2 <= bytes.Length;)
            {
                int tag = bytes[i], len = bytes[i + 1];
                if (i + 2 + len > bytes.Length) break;
                tags[tag] = tag <= 5 ? System.Text.Encoding.UTF8.GetString(bytes, i + 2, len) : Convert.ToBase64String(bytes, i + 2, len);
                i += 2 + len;
            }
            return new
            {
                seller = tags.GetValueOrDefault(1), vatNumber = tags.GetValueOrDefault(2), timestamp = tags.GetValueOrDefault(3), total = tags.GetValueOrDefault(4),
                vat = tags.GetValueOrDefault(5), hash = tags.GetValueOrDefault(6), hasSignature = tags.ContainsKey(7), hasPublicKey = tags.ContainsKey(8),
                phase = tags.ContainsKey(7) ? 2 : 1,
            };
        }
        catch (FormatException) { return new { invalid = true }; }
    }

    /* ══════════════════════════════════════ revenue and payments ══ */

    private static async Task<IResult> RevenueAsync(ControlStore control, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var tenants = (await control.Query(c).Query("control.tenant").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        var payments = (await control.Query(c).Query("control.payment").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        var paying = tenants.Where(t => (string)t["status"]! == "active" && !(bool)(t["is_trial"] ?? false)).ToList();
        var mrr = paying.Sum(t => L(t["mrr_h"]));
        var now = DateTime.UtcNow;
        var months = Enumerable.Range(0, 12).Select(i => new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(11 - i))).ToList();
        object By(Func<IDictionary<string, object?>, string?> key) => paying.GroupBy(t => key(t) ?? "—").Select(gr => new { label = gr.Key, clients = gr.Count(), mrrH = gr.Sum(t => L(t["mrr_h"])) })
            .OrderByDescending(x => x.mrrH).ToList();
        var due = tenants.Where(t => Dt(t["subscription_ends"]) is DateTime e && e >= now && e < now.AddDays(90) && (string)t["status"]! != "suspended").ToList();
        long Paid(DateTime from, DateTime to) => payments.Where(p => p["paid_on"] is DateTime d && d >= from && d < to).Sum(p => L(p["amount_h"]));
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return Results.Ok(new
        {
            mrrH = mrr, arrH = mrr * 12, payingClients = paying.Count, trials = tenants.Count(t => (bool)(t["is_trial"] ?? false)),
            suspendedMrrH = tenants.Where(t => (string)t["status"]! == "suspended").Sum(t => L(t["mrr_h"])),
            outstandingH = tenants.Sum(t => L(t["balance_h"])),
            paidThisMonthH = Paid(monthStart, monthStart.AddMonths(1)), paidLastMonthH = Paid(monthStart.AddMonths(-1), monthStart),
            arpuH = paying.Count == 0 ? 0 : mrr / paying.Count,
            byPlan = By(t => t["plan_code"] as string), byCity = By(t => t["city"] as string), byCycle = By(t => t["billing_cycle"] as string),
            byType = By(t => t["business_type"] as string),
            monthly = months.Select(m => new
            {
                month = m.ToString("yyyy-MM"), label = m.ToString("MMM yy", CultureInfo.InvariantCulture), paidH = Paid(m, m.AddMonths(1)),
                newClients = tenants.Count(t => Dt(t["created_at_utc"]) is DateTime d && d >= m && d < m.AddMonths(1)),
                /* MRR at the end of the month: clients created before it and not suspended now. */
                mrrH = tenants.Where(t => Dt(t["created_at_utc"]) < m.AddMonths(1) && (string)t["status"]! == "active" && !(bool)(t["is_trial"] ?? false)).Sum(t => L(t["mrr_h"])),
            }),
            renewalsDue = new
            {
                in30 = due.Count(t => Dt(t["subscription_ends"]) < now.AddDays(30)), in60 = due.Count(t => Dt(t["subscription_ends"]) < now.AddDays(60)), in90 = due.Count,
                valueIn30H = due.Where(t => Dt(t["subscription_ends"]) < now.AddDays(30)).Sum(t => L(t["mrr_h"]) * ((t["billing_cycle"] as string) == "annual" ? 12 : 1)),
                list = due.OrderBy(t => Dt(t["subscription_ends"])).Select(t => new
                {
                    clientId = Id(L(t["tenant_id"])), client = (string)t["name"]!, plan = t["plan_code"] as string, cycle = t["billing_cycle"] as string,
                    endsOn = Dt(t["subscription_ends"])?.ToString("yyyy-MM-dd"), days = (int)Math.Floor((Dt(t["subscription_ends"])!.Value - now).TotalDays), mrrH = L(t["mrr_h"]),
                }),
            },
        });
    }

    private static async Task<IResult> PaymentsAsync(ControlStore control, long? clientId, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var names = (await control.Query(c).Query("control.tenant").Select("tenant_id", "name").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>()
            .ToDictionary(r => L(r["tenant_id"]), r => (string)r["name"]!);
        var q = control.Query(c).Query("control.payment").OrderByDesc("paid_on").OrderByDesc("payment_id").Limit(1000);
        if (clientId is not null) q = q.Where("tenant_id", clientId);
        var rows = (await q.GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>();
        return Results.Ok(rows.Select(p => new
        {
            id = Id(L(p["payment_id"])), clientId = Id(L(p["tenant_id"])), client = names.GetValueOrDefault(L(p["tenant_id"]), "#" + p["tenant_id"]),
            amountH = L(p["amount_h"]), method = (string)p["method"]!, reference = p["reference"] as string, period = p["period"] as string, note = p["note"] as string,
            paidOn = p["paid_on"] is DateTime d ? d.ToString("yyyy-MM-dd") : null, recordedBy = p["recorded_by"] as string, recordedAt = Dt(p["recorded_at_utc"]),
        }));
    }

    private static async Task<IResult> RecordPaymentAsync(PaymentBody body, ControlStore control, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        if (body.AmountH <= 0) return Fail(422, "amount_required", "Enter the amount received.");
        await using var c = await control.OpenAsync(ct);
        var t = (await control.Query(c).Query("control.tenant").Where("tenant_id", body.ClientId).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();
        if (t is null) return Fail(404, "not_found", "That client does not exist.");
        var paidOn = DateTime.TryParse(body.PaidOn, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d.Date : DateTime.UtcNow.Date;
        var id = await control.Query(c).Query("control.payment").InsertGetIdAsync<long>(new Dictionary<string, object?>
        {
            ["tenant_id"] = body.ClientId, ["amount_h"] = body.AmountH, ["method"] = string.IsNullOrWhiteSpace(body.Method) ? "Bank transfer" : body.Method.Trim(),
            ["reference"] = body.Reference?.Trim(), ["period"] = body.Period?.Trim(), ["note"] = body.Note?.Trim(), ["paid_on"] = paidOn,
            ["recorded_by"] = user.Username, ["recorded_at_utc"] = DateTime.UtcNow,
        }, cancellationToken: ct);
        /* Whatever was owed comes down by what was paid. */
        var balance = Math.Max(0, L(t["balance_h"]) - body.AmountH);
        await control.Query(c).Query("control.tenant").Where("tenant_id", body.ClientId).UpdateAsync(new Dictionary<string, object?> { ["balance_h"] = balance, ["updated_at_utc"] = DateTime.UtcNow }, cancellationToken: ct);
        control.Forget();
        var audit = await ConsoleEndpoints.Record(control, body.ClientId, "payment.record", "Recorded payment", null, $"{body.AmountH / 100.0:0.00} SAR", body.Reference, user, http, ct);
        return Results.Ok(new { ok = true, id = Id(id), audit });
    }

    /* ══════════════════════════════════════ devices (registers seen in sessions) and issues ══ */

    private static async Task<IResult> DevicesAsync(ControlStore control, IDb db, CancellationToken ct)
    {
        var (ok, failed) = await EachAsync(db, await ClientsAsync(control, null, ct), async (cl, conn) => new
        {
            sessions = await Rows(db, conn, f => f.Query("sales.restaurant_session").Select("register_id", "branch_id", "opened_at_utc", "closed_at_utc", "opened_by", "status")
                .OrderByDesc("opened_at_utc").Limit(5000), ct),
            branches = await Rows(db, conn, f => f.Query("core.branch").Select("branch_id", "name_en"), ct),
        }, ct);
        var items = ok.SelectMany(x =>
        {
            var branch = x.Value.branches.ToDictionary(b => L(b["branch_id"]), b => b["name_en"] as string);
            return x.Value.sessions.Where(s => !string.IsNullOrEmpty(s["register_id"] as string)).GroupBy(s => (string)s["register_id"]!).Select(gr =>
            {
                var last = gr.First();
                var open = gr.Any(s => s["closed_at_utc"] is null);
                return new
                {
                    id = $"{x.Client.Id}-{gr.Key}", clientId = Id(x.Client.Id), clientName = x.Client.Name, name = gr.Key, branchName = branch.GetValueOrDefault(L(last["branch_id"])),
                    status = open ? "online" : "offline", lastSeenAt = gr.Max(s => Dt(s["closed_at_utc"]) ?? Dt(s["opened_at_utc"])), lastUser = last["opened_by"] as string, sessions = gr.Count(),
                };
            });
        }).OrderBy(d => d.clientName).ToList();
        return Results.Ok(new { items, note = "Devices are the registers seen opening sessions.", unreachable = failed });
    }

    private static async Task<IResult> IssuesAsync(ControlStore control, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var names = (await control.Query(c).Query("control.tenant").Select("tenant_id", "name").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>()
            .ToDictionary(r => L(r["tenant_id"]), r => (string)r["name"]!);
        var rows = (await control.Query(c).Query("control.error_log").Where("resolved", false).OrderByDesc("occurrences").Limit(300).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>();
        return Results.Ok(rows.Select(e => new
        {
            id = $"ERR-{L(e["error_id"])}", clientId = e["tenant_id"] is null ? null : Id(L(e["tenant_id"])),
            clientName = e["tenant_id"] is null ? "Ayn" : names.GetValueOrDefault(L(e["tenant_id"]), "#" + e["tenant_id"]),
            title = (string)e["message"]!, category = (string)e["source"]!, priority = Convert.ToInt32(e["occurrences"]) > 20 ? "high" : Convert.ToInt32(e["occurrences"]) > 3 ? "medium" : "low",
            status = "open", openedAt = Dt(e["first_at_utc"]), updatedAt = Dt(e["last_at_utc"]), occurrences = Convert.ToInt32(e["occurrences"]),
        }));
    }

    /* ══════════════════════════════════════ pulse: the quick look (overview + floating circle) ══ */

    private static async Task<IResult> PulseAsync(ControlStore control, IDb db, ServerSampler sampler, long? clientId, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        var load = await sampler.ReadAsync(ct);
        var clients = await ClientsAsync(control, clientId, ct);
        var (ok, failed) = await EachAsync(db, clients, async (cl, conn) => new
        {
            lastOrder = (await Rows(db, conn, f => f.Query("sales.sale").Where("status", "<>", "voided").Select("invoice_no", "total_h", "cashier_name", "sold_at_utc", "order_type").OrderByDesc("sold_at_utc").Limit(1), ct)).FirstOrDefault(),
            today = (await Rows(db, conn, f => f.Query("sales.sale").Where("sold_at_utc", ">=", today).Where("status", "<>", "voided").SelectRaw("COUNT(*) AS n, COALESCE(SUM(total_h),0) AS h"), ct)).First(),
            logins = await LoginRows(db, conn, today, 5000, ct),
            names = await Rows(db, conn, f => f.Query("core.app_user").Select("username", "email", "phone", "name_en"), ct),
            failures = (await Rows(db, conn, f => f.Query("audit.request_timing").Where("occurred_at_utc", ">=", hourAgo).Where("status_code", ">=", 500).SelectRaw("COUNT(*) AS n"), ct)).First(),
            open = (await Rows(db, conn, f => f.Query("sales.restaurant_session").WhereNull("closed_at_utc").SelectRaw("COUNT(*) AS n"), ct)).First(),
        }, ct);
        await using var c = await control.OpenAsync(ct);
        var openErrors = await control.Query(c).Query("control.error_log").Where("resolved", false).Where("last_at_utc", ">=", hourAgo).CountAsync<int>(cancellationToken: ct);
        string Name(IEnumerable<IDictionary<string, object?>> names, string? who) =>
            names.FirstOrDefault(n => who is not null && (Eq(n["username"], who) || Eq(n["email"], who) || Eq(n["phone"], who)))?["name_en"] as string ?? who ?? "Name not recorded";
        static bool Eq(object? a, string b) => a is string s && string.Equals(s, b, StringComparison.OrdinalIgnoreCase);
        var lastOrder = ok.Where(x => x.Value.lastOrder is not null).Select(x => (x.Client, o: x.Value.lastOrder!)).OrderByDescending(x => Dt(x.o["sold_at_utc"])).FirstOrDefault();
        var logins = ok.SelectMany(x => x.Value.logins.Select(l => (x.Client, l, name: Name(x.Value.names, l["identifier"] as string)))).OrderByDescending(x => Dt(x.l["attempted_at_utc"])).ToList();
        var lastLogin = logins.FirstOrDefault(x => (bool)x.l["succeeded"]!);
        var critical = new List<object>();
        foreach (var f in failed) critical.Add(new { kind = "database", text = $"Cannot reach the database of {((dynamic)f).client}", detail = (string)((dynamic)f).error });
        foreach (var x in ok.Where(x => L(x.Value.failures["n"]) > 0)) critical.Add(new { kind = "api", text = $"{x.Value.failures["n"]} server errors in the last hour at {x.Client.Name}", detail = "See Client → Failures" });
        if (openErrors > 0) critical.Add(new { kind = "errors", text = $"{openErrors} new error(s) in the last hour", detail = "See Health → Errors" });
        if (load.CpuPct >= 90 || load.MemoryPct >= 90 || load.DiskFreePct <= 8) critical.Add(new { kind = "server", text = $"Server under pressure: CPU {load.CpuPct}%, memory {load.MemoryPct}%, disk free {load.DiskFreePct}%", detail = "See Health → Usage" });
        return Results.Ok(new
        {
            at = DateTime.UtcNow,
            load = new { cpuPct = load.CpuPct, memoryPct = load.MemoryPct, diskUsedPct = Math.Round(100 - load.DiskFreePct, 1), dbConnections = load.DbConnections, requestsLastMinute = load.RequestsLastMinute },
            ordersToday = ok.Sum(x => L(x.Value.today["n"])), salesTodayH = ok.Sum(x => L(x.Value.today["h"])),
            usersSignedInToday = logins.Where(x => (bool)x.l["succeeded"]!).Select(x => $"{x.Client.Id}:{x.l["identifier"]}").Distinct().Count(),
            failedSignInsToday = logins.Count(x => !(bool)x.l["succeeded"]!),
            openSessions = ok.Sum(x => L(x.Value.open["n"])),
            lastOrder = lastOrder.o is null ? null : new
            {
                clientId = Id(lastOrder.Client.Id), client = lastOrder.Client.Name, invoiceNo = L(lastOrder.o["invoice_no"]), totalH = L(lastOrder.o["total_h"]),
                cashier = lastOrder.o["cashier_name"] as string, type = lastOrder.o["order_type"] as string, at = Dt(lastOrder.o["sold_at_utc"]),
            },
            lastLogin = lastLogin.l is null ? null : new { clientId = Id(lastLogin.Client.Id), client = lastLogin.Client.Name, user = lastLogin.name, login = lastLogin.l["identifier"] as string, at = Dt(lastLogin.l["attempted_at_utc"]), ip = lastLogin.l["ip_address"] as string },
            critical,
            clients = ok.Select(x => new
            {
                id = Id(x.Client.Id), name = x.Client.Name, ordersToday = L(x.Value.today["n"]), salesTodayH = L(x.Value.today["h"]),
                signedInToday = x.Value.logins.Where(l => (bool)l["succeeded"]!).Select(l => l["identifier"]).Distinct().Count(),
                openSessions = L(x.Value.open["n"]), serverErrorsLastHour = L(x.Value.failures["n"]),
                lastOrderAt = x.Value.lastOrder is null ? null : Dt(x.Value.lastOrder["sold_at_utc"]),
                active = L(x.Value.today["n"]) > 0 || x.Value.logins.Any(l => (bool)l["succeeded"]!),
            }).OrderByDescending(x => x.ordersToday).ToList(),
            unreachable = failed,
        });
    }

    /* ══════════════════════════════════════ Ayn administrators ══ */

    private static async Task<IResult> AdminsAsync(ControlStore control, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var rows = (await control.Query(c).Query("control.admin_user").OrderBy("admin_id").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>();
        var lastSeen = (await control.Query(c).Query("control.tenant_event").Select("actor").SelectRaw("MAX(at_utc) AS at").GroupBy("actor").GetAsync(cancellationToken: ct))
            .Cast<IDictionary<string, object?>>().Where(r => r["actor"] is string).ToDictionary(r => ((string)r["actor"]!).ToLowerInvariant(), r => Dt(r["at"]));
        return Results.Ok(rows.Select(a => new
        {
            id = Id(L(a["admin_id"])), name = (string)a["name"]!, email = (string)a["email"]!, username = a["username"] as string, role = (a["role"] as string) ?? "Super admin",
            active = (bool)(a["is_active"] ?? true), mustChangePassword = (bool)(a["must_change_password"] ?? false), createdAt = Dt(a["created_at_utc"]),
            lastActionAt = lastSeen.GetValueOrDefault(((string)a["email"]!).ToLowerInvariant()),
        }));
    }

    private static async Task<IResult> CreateAdminAsync(AdminBody body, ControlStore control, IPasswordHasher hasher, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var username = (body.Username ?? "").Trim().ToLowerInvariant();
        var email = (body.Email ?? "").Trim().ToLowerInvariant();
        if (username.Length < 3 || !email.Contains('@') || string.IsNullOrWhiteSpace(body.Name)) return Fail(422, "missing", "Name, a username (3+ characters) and an email are required.");
        await using var c = await control.OpenAsync(ct);
        var q = control.Query(c);
        if (await q.Query("control.admin_user").Where("email", email).OrWhere("username", username).ExistsAsync(cancellationToken: ct)) return Fail(409, "exists", "An administrator with that username or email already exists.");
        var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(9)).Replace('/', 'x').Replace('+', 'y');
        var (hash, salt) = hasher.Hash(password);
        await q.Query("control.admin_user").InsertAsync(new Dictionary<string, object?>
        {
            ["email"] = email, ["username"] = username, ["name"] = body.Name.Trim(), ["role"] = string.IsNullOrWhiteSpace(body.Role) ? "Super admin" : body.Role.Trim(),
            ["password_hash"] = hash, ["password_salt"] = salt, ["must_change_password"] = true, ["is_active"] = true, ["created_at_utc"] = DateTime.UtcNow,
        }, cancellationToken: ct);
        var audit = await ConsoleEndpoints.Record(control, null, "admin.create", $"Created administrator {username}", null, email, null, user, http, ct);
        return Results.Ok(new { ok = true, username, temporaryPassword = password, audit });
    }

    private static async Task<IResult> AdminStatusAsync(long adminId, UserStatusBody body, ControlStore control, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var q = control.Query(c);
        var row = (await q.Query("control.admin_user").Where("admin_id", adminId).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();
        if (row is null) return Fail(404, "not_found", "That administrator does not exist.");
        if (string.Equals((string)row["email"]!, user.Username, StringComparison.OrdinalIgnoreCase) && !body.Active) return Fail(422, "self", "You cannot deactivate your own account.");
        if (!body.Active && await q.Query("control.admin_user").Where("is_active", true).CountAsync<int>(cancellationToken: ct) <= 1) return Fail(422, "last_admin", "At least one active administrator must remain.");
        await q.Query("control.admin_user").Where("admin_id", adminId).UpdateAsync(new Dictionary<string, object?> { ["is_active"] = body.Active }, cancellationToken: ct);
        var audit = await ConsoleEndpoints.Record(control, null, body.Active ? "admin.activate" : "admin.deactivate", $"{(body.Active ? "Activated" : "Deactivated")} administrator {row["username"] ?? row["email"]}", null, null, body.Reason, user, http, ct);
        return Results.Ok(new { ok = true, audit });
    }

    /* ══════════════════════════════════════ search across everything ══ */

    private static async Task<IResult> FindAsync(string? q, ControlStore control, IDb db, CancellationToken ct)
    {
        var needle = (q ?? "").Trim();
        if (needle.Length < 2) return Results.Ok(new { groups = Array.Empty<object>() });
        var low = needle.ToLowerInvariant();
        var groups = new List<object>();
        await using var c = await control.OpenAsync(ct);
        var tenants = (await control.Query(c).Query("control.tenant").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        bool Hit(object? v) => v?.ToString()?.ToLowerInvariant().Contains(low) == true;
        var latest = PosApi.Data.Migrations.SchemaMigrator.LatestClientVersion();

        /* Clients: name, address, CR, VAT, GUID, contact. Each shows its database version. */
        var clientHits = tenants.Where(t => Hit(t["name"]) || Hit(t["slug"]) || Hit(t["name_ar"]) || Hit(t["cr_number"]) || Hit(t["vat_number"]) || Hit(t["client_guid"])
            || Hit(t["contact_email"]) || Hit(t["contact_phone"]) || Hit(t["contact_name"]) || Hit(t["city"])).Take(8).Select(t => new
            {
                kind = "client", title = (string)t["name"]!, to = $"/superadmin/clients/{t["tenant_id"]}",
                subtitle = $"{t["slug"]}.{control.Options.RootDomain} · {(t["plan_code"] as string) ?? "—"} · {t["status"]} · database v{t["schema_version"] ?? "—"}{(t["schema_version"] is null || L(t["schema_version"]) < latest ? " (update needed)" : "")}",
            }).ToList();
        if (clientHits.Count > 0) groups.Add(new { group = "Clients", items = clientHits });

        /* Versions: "version", "update" or "outdated" lists every client's database version. */
        if (new[] { "version", "update", "outdated", "upgrade" }.Any(w => low.Contains(w)))
            groups.Add(new
            {
                group = "Versions", items = tenants.OrderBy(t => L(t["schema_version"])).Select(t => new
                {
                    kind = "version", title = $"{t["name"]}: database v{t["schema_version"] ?? "not prepared"}", to = $"/superadmin/clients/{t["tenant_id"]}",
                    subtitle = t["schema_version"] is not null && L(t["schema_version"]) >= latest ? "Up to date" : $"Behind — latest is v{latest}",
                }).ToList(),
            });

        /* Health: "health", "slow", "down", "load". */
        if (new[] { "health", "slow", "down", "load", "cpu", "memory", "disk", "status" }.Any(w => low.Contains(w)))
            groups.Add(new
            {
                group = "Health check", items = new object[]
                {
                    new { kind = "health", title = "System health", subtitle = "Application, API, databases, sign-ins", to = "/superadmin/platform/health" },
                    new { kind = "health", title = "Server load", subtitle = "CPU, memory, disk, connections — today vs yesterday vs last week", to = "/superadmin/platform/usage" },
                    new { kind = "health", title = "Slow API calls", subtitle = "Every endpoint's speed", to = "/superadmin/audit/api" },
                    new { kind = "health", title = "Slow SQL queries", subtitle = "Which query, how long", to = "/superadmin/audit/sql" },
                },
            });

        /* Bugs: open errors whose message, page or client matches. */
        var errors = (await control.Query(c).Query("control.error_log").WhereLike("message", $"%{needle}%").OrWhereLike("url", $"%{needle}%")
            .OrderByDesc("last_at_utc").Limit(6).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        if (errors.Count > 0)
            groups.Add(new
            {
                group = "Errors", items = errors.Select(e => new
                {
                    kind = "error", title = (string)e["message"]!, to = "/superadmin/errors",
                    subtitle = $"{e["source"]} · seen {e["occurrences"]}× · {(e["resolved"] is true ? "resolved" : "open")} · {e["url"]}",
                }).ToList(),
            });

        /* Orders: an invoice number is looked up in every client. */
        if (long.TryParse(needle.TrimStart('#'), out var invoice))
        {
            var (ok, _) = await EachAsync(db, await ClientsAsync(control, null, ct), (cl, conn) => Rows(db, conn, f => f.Query("sales.sale").Where("invoice_no", invoice)
                .Select("invoice_no", "order_no", "status", "total_h", "cashier_name", "sold_at_utc"), ct), ct);
            var orders = ok.SelectMany(x => x.Value.Select(s => new
            {
                kind = "order", title = $"Invoice {s["invoice_no"]} at {x.Client.Name}", to = $"/superadmin/clients/{x.Client.Id}",
                subtitle = $"{s["status"]} · {L(s["total_h"]) / 100.0:0.00} SAR · {s["cashier_name"]} · {Dt(s["sold_at_utc"]):dd MMM HH:mm}",
            })).ToList();
            if (orders.Count > 0) groups.Add(new { group = "Orders", items = orders });
        }

        /* People: client users by name, username, email or phone. */
        if (needle.Length >= 3)
        {
            var (ok, _) = await EachAsync(db, await ClientsAsync(control, null, ct), (cl, conn) => Rows(db, conn, f => f.Query("core.app_user")
                .Where(w => w.WhereLike("name_en", $"%{needle}%").OrWhereLike("username", $"%{needle}%").OrWhereLike("email", $"%{needle}%").OrWhereLike("phone", $"%{needle}%"))
                .Select("name_en", "username", "role_id", "status").Limit(5), ct), ct);
            var people = ok.SelectMany(x => x.Value.Select(u => new
            {
                kind = "user", title = $"{u["name_en"] ?? u["username"]}", to = $"/superadmin/clients/{x.Client.Id}",
                subtitle = $"{x.Client.Name} · {u["role_id"]} · {u["username"]} · {u["status"]}",
            })).Take(8).ToList();
            if (people.Count > 0) groups.Add(new { group = "Users", items = people });
        }

        /* Pages: jump to a screen by its name. */
        var screens = new (string Title, string To, string Words)[]
        {
            ("Revenue", "/superadmin/revenue", "revenue money payments mrr arr"), ("ZATCA", "/superadmin/zatca", "zatca fatoora einvoice qr"),
            ("Sessions", "/superadmin/sessions", "session open close shift day"), ("Order speed", "/superadmin/orders/speed", "order speed print kitchen time"),
            ("Errors", "/superadmin/errors", "error bug crash exception"), ("Client activity", "/superadmin/analytics/activity", "activity login sign"),
            ("Recent activity", "/superadmin/audit/activity", "recent activity audit"), ("New client", "/superadmin/clients/new", "new client add create"),
        };
        var screenHits = screens.Where(s => s.Words.Split(' ').Any(w => w.StartsWith(low, StringComparison.Ordinal) || low.Contains(w))).Select(s => new { kind = "page", title = s.Title, subtitle = "Open screen", to = s.To }).ToList();
        if (screenHits.Count > 0) groups.Add(new { group = "Screens", items = screenHits });

        return Results.Ok(new { groups });
    }
}
