using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;
using PosApi.Data.Migrations;
using PosApi.Features.Access;
using PosApi.Tenancy;
using SqlKata.Execution;

namespace PosApi.Features.SuperAdmin;

/// <summary>
/// The POSSA console (admin.wateeq.com): every figure from the control database or, live, from each client's own
/// database. Paths match the console's services (/api/sa/...); shapes match what its screens read.
/// </summary>
public static class ConsoleEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /* Health rules: each band is the result of stated rules, and each client carries the rules it failed. */
    private static readonly (string Key, string Label, string Weight)[] Rules =
    [
        ("unreachable", "Database cannot be reached", "critical"),
        ("suspended", "Subscription suspended", "critical"),
        ("payment", "Payment overdue", "critical"),
        ("expired", "Subscription expired", "critical"),
        ("no_activity", "No activity for 7 days or more", "critical"),
        ("setup", "Setup not finished (no owner yet)", "attention"),
        ("outdated", "Database needs an update", "attention"),
        ("expiring", "Subscription expires within 14 days", "attention"),
        ("api_errors", "API error rate above 2%", "attention"),
        ("quiet", "No activity for 2 to 6 days", "attention"),
    ];

    public sealed record SuspendBody(string? Reason);
    public sealed record RenewBody(int? Days);

    public static void MapConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sa").WithTags("POSSA console").RequireRateLimiting("standard")
            .RequireAuthorization(p => p.RequireRole(SuperAdminEndpoints.Role));

        g.MapGet("/overview", OverviewAsync);
        g.MapGet("/dashboard", OverviewAsync);
        g.MapGet("/activity", ActivityAsync);
        g.MapGet("/clients", ClientsAsync);
        g.MapGet("/clients/attention", AttentionAsync);
        g.MapGet("/clients/health", HealthBandsAsync);
        g.MapGet("/clients/growth", GrowthAsync);
        g.MapGet("/clients/{id:long}", ClientAsync);
        g.MapGet("/clients/{id:long}/branches", BranchesAsync);
        g.MapGet("/clients/{id:long}/users", UsersAsync);
        g.MapGet("/clients/{id:long}/devices", (long id) => Results.Ok(Array.Empty<object>()));
        g.MapGet("/clients/{id:long}/hardware", (long id) => Results.Ok(Array.Empty<object>()));
        g.MapGet("/clients/{id:long}/usage", UsageAsync);
        g.MapGet("/clients/{id:long}/subscription", SubscriptionAsync);
        g.MapGet("/clients/{id:long}/issues", (long id) => Results.Ok(Array.Empty<object>()));
        g.MapGet("/clients/{id:long}/activity", (long id, ControlStore c, CancellationToken ct) => AuditRowsAsync(c, id, 40, ct));
        g.MapGet("/clients/{id:long}/transactions", TransactionsAsync);
        g.MapPost("/clients", CreateAsync);
        g.MapPost("/clients/{id:long}", UpdateAsync);
        g.MapPost("/clients/{id:long}/suspend", (long id, SuspendBody b, ControlStore c, ClientMetrics m, ICurrentUser u, HttpContext h, CancellationToken ct) =>
            StatusAsync(c, m, u, h, id, "suspended", "client.suspend", "Suspended client", b.Reason, ct));
        g.MapPost("/clients/{id:long}/activate", (long id, ControlStore c, ClientMetrics m, ICurrentUser u, HttpContext h, CancellationToken ct) =>
            StatusAsync(c, m, u, h, id, "active", "client.activate", "Activated client", null, ct));
        g.MapPost("/clients/{id:long}/restore", (long id, SuspendBody b, ControlStore c, ClientMetrics m, ICurrentUser u, HttpContext h, CancellationToken ct) =>
            StatusAsync(c, m, u, h, id, "active", "client.restore", "Restored client", b.Reason, ct));
        g.MapPost("/clients/{id:long}/support-session", async (long id, SuspendBody b, ControlStore c, ICurrentUser u, HttpContext h, CancellationToken ct) =>
        {
            var audit = await Record(c, id, "support.session", "Started support session", null, "active", b.Reason, u, h, ct);
            return Results.Ok(new { ok = true, audit });
        });

        g.MapGet("/subscriptions", SubscriptionsAsync);
        g.MapGet("/subscriptions/expiring", ExpiringAsync);
        g.MapPost("/subscriptions/{id:long}/renew", RenewAsync);
        g.MapPost("/subscriptions/{id:long}/cancel", CancelAsync);
        g.MapGet("/subscriptions/plans", PlansAsync);
        g.MapGet("/master/plans", PlansAsync);
        g.MapGet("/master/{list}", MasterAsync);

        g.MapGet("/audit", (long? clientId, ControlStore c, CancellationToken ct) => AuditRowsAsync(c, clientId, 500, ct));
        g.MapGet("/notifications", NotificationsAsync);
        g.MapGet("/search", SearchAsync);
        g.MapGet("/platform/health", PlatformHealthAsync);
    }

    /* ------------------------------------------------------------------ rows */

    private sealed record Loaded(List<IDictionary<string, object?>> Rows, Dictionary<long, ClientFigures> Figures, long Latest);

    private static async Task<Loaded> LoadAsync(ControlStore control, ClientMetrics metrics, bool fresh, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var rows = (await control.Query(c).Query("control.tenant").OrderBy("tenant_id").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        var figures = await metrics.ForAllAsync(rows.Select(control.ToTenant), fresh, ct);
        return new Loaded(rows, figures, SchemaMigrator.LatestClientVersion());
    }

    private static JsonObject Row(IDictionary<string, object?> r, ClientFigures? f, long latest, ControlStore control)
    {
        var id = Convert.ToInt64(r["tenant_id"]);
        var now = DateTime.UtcNow;
        var created = Dt(r["created_at_utc"])!.Value;
        var ends = Dt(r["subscription_ends"]);
        var daysToRenewal = ends is null ? 9999 : (int)Math.Floor((ends.Value - now).TotalDays);
        var lastActivity = new[] { f?.LastTransactionAt, f?.LastLoginAt }.Where(d => d is not null).Max();
        var daysSince = (int)Math.Floor((now - (lastActivity ?? created)).TotalDays);
        var status = (string)r["status"]!;
        var isTrial = (bool)(r["is_trial"] ?? false);
        var shownStatus = status == "suspended" ? "suspended" : isTrial ? "trial" : "active";
        var balance = L(r["balance_h"]);

        var failed = new List<(string Key, string Label, string Weight)>();
        foreach (var rule in Rules)
        {
            var hit = rule.Key switch
            {
                "unreachable" => f is not null && !f.Reachable,
                "suspended" => status == "suspended",
                "payment" => balance > 0,
                "expired" => ends is not null && daysToRenewal < 0,
                "no_activity" => f?.Reachable == true && status != "setup" && daysSince >= 7,
                "setup" => status == "setup",
                "outdated" => r["schema_version"] is null || L(r["schema_version"]) < latest,
                "expiring" => ends is not null && daysToRenewal is >= 0 and <= 14,
                "api_errors" => f is not null && f.ApiErrorRate > 2,
                "quiet" => f?.Reachable == true && status != "setup" && daysSince is >= 2 and < 7,
                _ => false,
            };
            if (hit) failed.Add(rule);
        }
        var band = failed.Any(x => x.Weight == "critical") ? "critical" : failed.Count > 0 ? "attention" : "healthy";
        var slug = (string)r["slug"]!;

        return new JsonObject
        {
            ["id"] = id.ToString(CultureInfo.InvariantCulture), ["slug"] = slug, ["guid"] = r.TryGetValue("client_guid", out var guid) ? guid as string : null,
            ["url"] = $"https://{(r["custom_host"] as string) ?? $"{slug}.{control.Options.RootDomain}"}",
            ["name"] = (string)r["name"]!, ["nameAr"] = (r["name_ar"] as string) ?? (string)r["name"]!,
            ["plan"] = (r["plan_code"] as string) ?? (r["plan"] as string) ?? "Basic",
            ["region"] = r["city"] as string, ["industry"] = r["business_type"] as string, ["businessType"] = r["business_type"] as string,
            ["status"] = shownStatus, ["setupStatus"] = status,
            ["branches"] = f?.Branches ?? 0, ["devices"] = 0, ["devicesOnline"] = 0,
            ["users"] = f?.Users ?? 0, ["transactions"] = f?.Transactions30d ?? 0, ["salesH"] = f?.SalesH30d ?? 0,
            ["mrrH"] = L(r["mrr_h"]),
            ["createdOn"] = created.ToString("yyyy-MM-dd"), ["subscriptionEnds"] = ends?.ToString("yyyy-MM-dd"),
            ["daysToRenewal"] = daysToRenewal, ["daysSinceActivity"] = daysSince,
            ["apiErrorRate"] = f?.ApiErrorRate ?? 0, ["paymentOverdue"] = balance > 0, ["balanceH"] = balance,
            ["poc"] = new JsonObject
            {
                ["name"] = r["contact_name"] as string, ["username"] = r["poc_username"] as string, ["mobile"] = r["contact_phone"] as string,
                ["email"] = r["contact_email"] as string, ["role"] = (r["poc_role"] as string) ?? "Owner", ["status"] = "active",
                ["lastLoginAt"] = f?.LastLoginAt,
            },
            ["paymentMethods"] = new JsonArray(((r["payment_methods"] as string) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => (JsonNode)x!).ToArray()),
            ["vatRate"] = Convert.ToInt32(r["vat_rate"] ?? 15), ["crNumber"] = r["cr_number"] as string, ["vatNumber"] = r["vat_number"] as string,
            ["registeredBy"] = r["registered_by"] as string, ["activatedBy"] = r["activated_by"] as string,
            ["isTrial"] = isTrial, ["trialDays"] = r["trial_days"] is null ? null : Convert.ToInt32(r["trial_days"]),
            ["firstLoginOn"] = f?.FirstSaleAt?.ToString("yyyy-MM-dd"), ["lastRenewedOn"] = Dt(r["last_renewed_at_utc"])?.ToString("yyyy-MM-dd"),
            ["renewals"] = Convert.ToInt32(r["renewals"] ?? 0),
            ["lastLoginAt"] = f?.LastLoginAt, ["lastTransactionAt"] = f?.LastTransactionAt,
            ["storageGb"] = f?.DatabaseBytes is long b ? Math.Round(b / 1_073_741_824d, 2) : 0,
            ["reachable"] = f?.Reachable ?? false, ["readError"] = f?.Error,
            ["schemaVersion"] = r["schema_version"] is null ? null : L(r["schema_version"]), ["upToDate"] = r["schema_version"] is not null && L(r["schema_version"]) >= latest,
            ["db"] = new JsonObject { ["host"] = (string)r["db_host"]!, ["port"] = Convert.ToInt32(r["db_port"]), ["database"] = (string)r["db_name"]!, ["user"] = (string)r["db_user"]! },
            ["health"] = new JsonObject
            {
                ["band"] = band,
                ["reasons"] = new JsonArray(failed.Select(x => (JsonNode)x.Label!).ToArray()),
                ["rules"] = new JsonArray(failed.Select(x => (JsonNode)x.Key!).ToArray()),
            },
        };
    }

    private static async Task<IResult> ClientsAsync(ControlStore control, ClientMetrics metrics, bool? fresh, CancellationToken ct)
    {
        var data = await LoadAsync(control, metrics, fresh == true, ct);
        return Results.Ok(data.Rows.Select(r => Row(r, data.Figures.GetValueOrDefault(Convert.ToInt64(r["tenant_id"])), data.Latest, control)));
    }

    private static async Task<IResult> ClientAsync(long id, ControlStore control, ClientMetrics metrics, CancellationToken ct)
    {
        var r = await Find(control, id, ct);
        if (r is null) return NotFound();
        var f = await metrics.ForAsync(control.ToTenant(r), fresh: true, ct);
        return Results.Ok(Row(r, f, SchemaMigrator.LatestClientVersion(), control));
    }

    /* ------------------------------------------------------------------ overview */

    private static async Task<IResult> OverviewAsync(ControlStore control, ClientMetrics metrics, string? range, CancellationToken ct)
    {
        var data = await LoadAsync(control, metrics, false, ct);
        var rows = data.Rows.Select(r => Row(r, data.Figures.GetValueOrDefault(Convert.ToInt64(r["tenant_id"])), data.Latest, control)).ToList();
        var start = DateTime.UtcNow.AddDays(-30);
        bool Before(JsonObject x) => DateTime.Parse((string)x["createdOn"]!, CultureInfo.InvariantCulture) < start;
        object Pair(double total, double previous) => new { total, previous };
        var today = range is "today" or "yesterday";
        var f = data.Figures.Values.ToList();
        var reachable = f.Count(x => x.Reachable);
        return Results.Ok(new
        {
            clients = Pair(rows.Count, rows.Count(Before)),
            active = Pair(rows.Count(x => (string)x["status"]! == "active"), rows.Count(x => Before(x) && (string)x["status"]! == "active")),
            trial = Pair(rows.Count(x => (string)x["status"]! == "trial"), rows.Count(x => Before(x) && (string)x["status"]! == "trial")),
            suspended = Pair(rows.Count(x => (string)x["status"]! == "suspended"), rows.Count(x => Before(x) && (string)x["status"]! == "suspended")),
            devices = Pair(0, 0),
            devicesOnline = Pair(0, 0),
            transactions = today ? Pair(f.Sum(x => x.TransactionsToday), 0) : Pair(f.Sum(x => x.Transactions30d), f.Sum(x => x.TransactionsPrev30d)),
            clientSalesH = Pair(f.Sum(x => x.SalesH30d), f.Sum(x => x.SalesHPrev30d)),
            mrrH = Pair(rows.Where(x => (string)x["status"]! != "suspended").Sum(x => (long)x["mrrH"]!), rows.Where(x => Before(x) && (string)x["status"]! != "suspended").Sum(x => (long)x["mrrH"]!)),
            expiring = Pair(rows.Count(x => (int)x["daysToRenewal"]! is >= 0 and <= 14), 0),
            uptime = Pair(f.Count == 0 ? 100 : Math.Round(reachable * 100.0 / f.Count, 2), 100),
            openIssues = Pair(rows.Count(x => (string)x["health"]!["band"]! == "critical"), 0),
        });
    }

    private static async Task<IResult> AttentionAsync(ControlStore control, ClientMetrics metrics, CancellationToken ct)
    {
        var data = await LoadAsync(control, metrics, false, ct);
        var rows = data.Rows.Select(r => Row(r, data.Figures.GetValueOrDefault(Convert.ToInt64(r["tenant_id"])), data.Latest, control))
            .Where(x => ((JsonArray)x["health"]!["reasons"]!).Count > 0)
            .OrderBy(x => (string)x["health"]!["band"]! == "critical" ? 0 : 1).Take(8)
            .Select(x =>
            {
                var reasons = ((JsonArray)x["health"]!["reasons"]!).Select(n => (string)n!).ToList();
                return new { id = (string)x["id"]!, name = (string)x["name"]!, plan = (string)x["plan"]!, issue = reasons[0], otherIssues = reasons.Skip(1), priority = (string)x["health"]!["band"]! == "critical" ? "high" : "medium" };
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> HealthBandsAsync(ControlStore control, ClientMetrics metrics, CancellationToken ct)
    {
        var data = await LoadAsync(control, metrics, false, ct);
        var counts = new Dictionary<string, int> { ["healthy"] = 0, ["attention"] = 0, ["critical"] = 0, ["inactive"] = 0 };
        foreach (var r in data.Rows)
        {
            var row = Row(r, data.Figures.GetValueOrDefault(Convert.ToInt64(r["tenant_id"])), data.Latest, control);
            if ((string)row["status"]! == "suspended") counts["inactive"]++;
            else counts[(string)row["health"]!["band"]!]++;
        }
        return Results.Ok(new { counts, rules = Rules.Select(r => new { key = r.Key, label = r.Label, weight = r.Weight }) });
    }

    private static async Task<IResult> GrowthAsync(ControlStore control, string? range, CancellationToken ct)
    {
        var months = range == "12m";
        var n = range switch { "today" or "yesterday" => 1, "7d" => 7, "90d" => 90, "12m" => 12, _ => 30 };
        await using var c = await control.OpenAsync(ct);
        var tenants = (await control.Query(c).Query("control.tenant").Select("created_at_utc", "activated_at_utc").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        var since = months ? DateTime.UtcNow.AddMonths(-n) : DateTime.UtcNow.Date.AddDays(-n + 1);
        var events = (await control.Query(c).Query("control.tenant_event").Where("at_utc", ">=", since).WhereIn("kind", new[] { "suspended", "client.suspend", "deactivated" })
            .Select("kind", "at_utc").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        string Key(DateTime d) => months ? d.ToString("yyyy-MM") : d.ToString("yyyy-MM-dd");
        var series = Enumerable.Range(0, n).Select(i =>
        {
            var d = months ? DateTime.UtcNow.AddMonths(-(n - 1 - i)) : DateTime.UtcNow.Date.AddDays(-(n - 1 - i));
            var k = Key(d);
            var created = tenants.Count(t => Key(Dt(t["created_at_utc"])!.Value) == k);
            var activated = tenants.Count(t => Dt(t["activated_at_utc"]) is DateTime a && Key(a) == k);
            var suspended = events.Count(e => Key(Dt(e["at_utc"])!.Value) == k);
            return new { label = months ? d.ToString("MMM", CultureInfo.InvariantCulture) : d.ToString("MM-dd"), date = d.ToString("yyyy-MM-dd"), value = created, created, activated, churned = 0, suspended };
        }).ToList();
        return Results.Ok(new
        {
            series,
            totals = new { created = series.Sum(s => s.created), activated = series.Sum(s => s.activated), churned = 0, suspended = series.Sum(s => s.suspended), net = series.Sum(s => s.created) - series.Sum(s => s.suspended) },
        });
    }

    private static async Task<IResult> ActivityAsync(ControlStore control, int? take, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var names = await Names(control, c, ct);
        var rows = await control.Query(c).Query("control.tenant_event").OrderByDesc("event_id").Limit(Math.Clamp(take ?? 12, 1, 200)).GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Cast<IDictionary<string, object?>>().Select(e =>
        {
            var tid = e["tenant_id"] is null ? (long?)null : L(e["tenant_id"]);
            var kind = (string)e["kind"]!;
            return new
            {
                id = $"ev_{L(e["event_id"])}", at = Dt(e["at_utc"]), kind, label = (e["label"] as string) ?? Pretty(kind),
                clientId = tid?.ToString(CultureInfo.InvariantCulture), clientName = tid is null ? "POSSA" : names.GetValueOrDefault(tid.Value, $"#{tid}"),
                detail = e["detail"] as string, severity = kind.Contains("fail") || kind.Contains("suspend") ? "warning" : "info",
            };
        }));
    }

    /* ------------------------------------------------------------------ one client */

    private static async Task<IResult> BranchesAsync(long id, ControlStore control, IDb db, CancellationToken ct) =>
        await InClient(control, id, ct, async (t, conn) =>
        {
            var q = db.Query(conn);
            var since = DateTime.UtcNow.AddDays(-30);
            var counts = (await q.Query("sales.sale").Where("status", "<>", "voided").Where("sold_at_utc", ">=", since)
                .Select("branch_id").SelectRaw("COUNT(*) AS n").GroupBy("branch_id").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>()
                .ToDictionary(x => L(x["branch_id"]), x => L(x["n"]));
            var rows = await q.Query("core.branch").OrderBy("branch_id").GetAsync(cancellationToken: ct);
            return rows.Cast<IDictionary<string, object?>>().Select(b => (object)new
            {
                id = L(b["branch_id"]).ToString(CultureInfo.InvariantCulture), clientId = id.ToString(CultureInfo.InvariantCulture), clientName = t.Name,
                name = (b["name_en"] as string) ?? (b["name_ar"] as string), nameAr = b["name_ar"] as string, code = b["code"] as string,
                city = b.TryGetValue("city", out var city) ? city as string : null,
                devices = 0, devicesOnline = 0, users = 0, transactions = counts.GetValueOrDefault(L(b["branch_id"])),
            }).ToList();
        }, db);

    private static async Task<IResult> UsersAsync(long id, ControlStore control, IDb db, CancellationToken ct) =>
        await InClient(control, id, ct, async (t, conn) =>
        {
            var q = db.Query(conn);
            var roles = new Dictionary<string, string>();
            try
            {
                foreach (var r in (await q.Query("core.role").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>())
                    roles[(string)r["role_id"]!] = (r.TryGetValue("name_en", out var n) ? n as string : null) ?? (string)r["role_id"]!;
            }
            catch { /* roles table missing: show the code */ }
            var rows = (await q.Query("core.app_user").OrderBy("user_id").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
            /* The admin: the account Ayn created (the owner). Everyone else was created by the client. */
            var firstId = rows.Select(u => Convert.ToString(u["user_id"], CultureInfo.InvariantCulture)).FirstOrDefault();
            return rows.Select(u => (object)new
            {
                isAdmin = ((string?)u["role_id"] ?? "").Contains("owner", StringComparison.OrdinalIgnoreCase) || Convert.ToString(u["user_id"], CultureInfo.InvariantCulture) == firstId,
                createdBy = Convert.ToString(u["user_id"], CultureInfo.InvariantCulture) == firstId ? "Ayn" : "Client",
                id = Convert.ToString(u["user_id"], CultureInfo.InvariantCulture), clientId = id.ToString(CultureInfo.InvariantCulture), clientName = t.Name, scope = "client",
                name = (u["name_en"] as string) ?? (u["name_ar"] as string), username = u["username"] as string, email = u["email"] as string, mobile = u["phone"] as string,
                role = roles.GetValueOrDefault((string?)u["role_id"] ?? "", (string?)u["role_id"] ?? ""), branchId = (string?)null, branchName = (string?)null,
                lastLoginAt = u.TryGetValue("last_login_at_utc", out var ll) ? Dt(ll) : null, status = (u["status"] as string) ?? "active",
            }).ToList();
        }, db);

    private static async Task<IResult> TransactionsAsync(long id, ControlStore control, IDb db, int? take, CancellationToken ct) =>
        await InClient(control, id, ct, async (t, conn) =>
        {
            var q = db.Query(conn);
            var limit = Math.Clamp(take ?? 240, 1, 1000);
            var sales = (await q.Query("sales.sale").OrderByDesc("sale_id").Limit(limit).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
            var ids = sales.Select(s => L(s["sale_id"])).ToArray();
            var branches = (await q.Query("core.branch").Select("branch_id", "name_en", "name_ar").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>()
                .ToDictionary(b => L(b["branch_id"]), b => (b["name_en"] as string) ?? (b["name_ar"] as string));
            var methods = ids.Length == 0 ? new Dictionary<long, string>() : (await q.Query("sales.sale_payment").WhereIn("sale_id", ids).Select("sale_id", "method").GetAsync(cancellationToken: ct))
                .Cast<IDictionary<string, object?>>().GroupBy(p => L(p["sale_id"])).ToDictionary(g => g.Key, g => string.Join(" + ", g.Select(p => (string)p["method"]!).Distinct()));
            var items = ids.Length == 0 ? new Dictionary<long, long>() : (await q.Query("sales.sale_line").WhereIn("sale_id", ids).Select("sale_id").SelectRaw("COUNT(*) AS n").GroupBy("sale_id").GetAsync(cancellationToken: ct))
                .Cast<IDictionary<string, object?>>().ToDictionary(x => L(x["sale_id"]), x => L(x["n"]));
            return sales.Select(s =>
            {
                var sid = L(s["sale_id"]);
                var status = (string)s["status"]!;
                var total = L(s["total_h"]); var vat = L(s["tax_h"]);
                return (object)new
                {
                    id = sid.ToString(CultureInfo.InvariantCulture), invoiceNo = Convert.ToString(s["invoice_no"], CultureInfo.InvariantCulture), at = Dt(s["sold_at_utc"]),
                    branchId = Convert.ToString(s["branch_id"], CultureInfo.InvariantCulture), branchName = branches.GetValueOrDefault(L(s["branch_id"])),
                    deviceId = (string?)null, cashier = s["cashier_name"] as string, items = items.GetValueOrDefault(sid), method = methods.GetValueOrDefault(sid, "—"),
                    netH = total - vat, vatH = vat, totalH = total,
                    signedTotalH = status == "voided" ? 0 : total,
                    status = status == "voided" ? "void" : status is "returned" or "partially_returned" ? "refunded" : "completed",
                    customer = s["customer_id"] is null ? "Walk-in" : "Account",
                };
            }).ToList();
        }, db);

    private static async Task<IResult> UsageAsync(long id, ControlStore control, ClientMetrics metrics, CancellationToken ct)
    {
        var r = await Find(control, id, ct);
        if (r is null) return NotFound();
        var f = await metrics.ForAsync(control.ToTenant(r), fresh: false, ct);
        var limits = await PlanLimits(control, (r["plan_code"] as string) ?? "Basic", ct);
        object Meter(double used, double? limit, double previous) => new { used, limit, percent = limit is null or 0 ? (double?)null : Math.Round(used * 100 / limit.Value), previous };
        var gb = f.DatabaseBytes is long b ? Math.Round(b / 1_073_741_824d, 2) : 0;
        return Results.Ok(new
        {
            branches = Meter(f.Branches, limits.GetValueOrDefault("branches"), f.Branches),
            devices = Meter(0, limits.GetValueOrDefault("devices"), 0),
            users = Meter(f.Users, limits.GetValueOrDefault("users"), f.Users),
            transactions = Meter(f.Transactions30d, limits.GetValueOrDefault("transactions"), f.TransactionsPrev30d),
            storage = Meter(gb, limits.GetValueOrDefault("storageGb"), gb),
            database = new { sizeGb = gb, rows = (long?)null, tables = (int?)null, largestTable = (string?)null, largestTableRows = (long?)null },
            files = new { sizeGb = 0, count = 0 },
            growth = new
            {
                storagePct = (double?)null,
                transactionsPct = f.TransactionsPrev30d == 0 ? (double?)null : Math.Round((f.Transactions30d - f.TransactionsPrev30d) * 100.0 / f.TransactionsPrev30d, 1),
                rowsPerDay = (long?)null,
            },
        });
    }

    private static async Task<IResult> SubscriptionAsync(long id, ControlStore control, CancellationToken ct)
    {
        var r = await Find(control, id, ct);
        return r is null ? NotFound() : Results.Ok(await Subscription(control, r, ct));
    }

    private static async Task<object> Subscription(ControlStore control, IDictionary<string, object?> r, CancellationToken ct)
    {
        var ends = Dt(r["subscription_ends"]);
        var plan = (r["plan_code"] as string) ?? "Basic";
        var mrr = L(r["mrr_h"]);
        var status = (string)r["status"]! == "suspended" ? "suspended" : (bool)(r["is_trial"] ?? false) ? "trial" : "active";
        return new
        {
            id = $"SUB-{L(r["tenant_id"])}", clientId = Convert.ToString(r["tenant_id"], CultureInfo.InvariantCulture), clientName = (string)r["name"]!, plan,
            cycle = (r["billing_cycle"] as string) ?? "monthly",
            startsOn = (Dt(r["subscription_starts"]) ?? Dt(r["created_at_utc"]))?.ToString("yyyy-MM-dd"), renewsOn = ends?.ToString("yyyy-MM-dd"),
            daysToRenewal = ends is null ? 9999 : (int)Math.Floor((ends.Value - DateTime.UtcNow).TotalDays),
            mrrH = mrr, arrH = mrr * 12, status, limits = await PlanLimits(control, plan, ct),
        };
    }

    /* ------------------------------------------------------------------ writes */

    private static async Task<IResult> StatusAsync(ControlStore control, ClientMetrics metrics, ICurrentUser user, HttpContext http, long id,
        string next, string action, string label, string? reason, CancellationToken ct)
    {
        var r = await Find(control, id, ct);
        if (r is null) return NotFound();
        if (next == "suspended" && string.IsNullOrWhiteSpace(reason))
            return Error(422, "reason_required", "Give a reason for suspending this client.", "اذكر سبب إيقاف العميل.");
        var before = (string)r["status"]!;
        await using (var c = await control.OpenAsync(ct))
        {
            var values = new Dictionary<string, object?> { ["status"] = next, ["status_reason"] = reason, ["updated_at_utc"] = DateTime.UtcNow };
            if (next == "active" && r["activated_at_utc"] is null) { values["activated_at_utc"] = DateTime.UtcNow; values["activated_by"] = user.Username; }
            await control.Query(c).Query("control.tenant").Where("tenant_id", id).UpdateAsync(values, cancellationToken: ct);
        }
        control.Forget(); metrics.Forget(id);
        var audit = await Record(control, id, action, label, before, next, reason, user, http, ct);
        return Results.Ok(new { ok = true, audit });
    }

    private static async Task<IResult> UpdateAsync(long id, JsonObject body, ControlStore control, ClientMetrics metrics, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var r = await Find(control, id, ct);
        if (r is null) return NotFound();
        var map = new Dictionary<string, string>
        {
            ["name"] = "name", ["companyName"] = "name", ["nameAr"] = "name_ar", ["businessType"] = "business_type", ["industry"] = "business_type",
            ["city"] = "city", ["region"] = "city", ["plan"] = "plan_code", ["cycle"] = "billing_cycle", ["cr"] = "cr_number", ["crNumber"] = "cr_number",
            ["vat"] = "vat_number", ["vatNumber"] = "vat_number", ["email"] = "contact_email", ["phone"] = "contact_phone", ["notes"] = "notes",
        };
        var values = new Dictionary<string, object?> { ["updated_at_utc"] = DateTime.UtcNow };
        foreach (var (k, col) in map) if (body[k] is JsonValue v && v.TryGetValue<string>(out var s)) values[col] = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        if (body["paymentMethods"] is JsonArray pm) values["payment_methods"] = string.Join(",", pm.Select(x => x!.ToString()));
        if (body["defaultVat"] is JsonValue dv && dv.TryGetValue<int>(out var vat)) values["vat_rate"] = vat;
        if (body["vatRate"] is JsonValue vr && vr.TryGetValue<int>(out var vat2)) values["vat_rate"] = vat2;
        if (body["mrrH"] is JsonValue mv && mv.TryGetValue<long>(out var mrr)) values["mrr_h"] = mrr;
        if (body["poc"] is JsonObject poc)
        {
            if (poc["name"] is JsonValue pn) values["contact_name"] = pn.ToString();
            if (poc["email"] is JsonValue pe) values["contact_email"] = pe.ToString();
            if (poc["mobile"] is JsonValue pmb) values["contact_phone"] = pmb.ToString();
            if (poc["username"] is JsonValue pu) values["poc_username"] = pu.ToString();
            if (poc["role"] is JsonValue pr) values["poc_role"] = pr.ToString();
        }
        if (values.TryGetValue("plan_code", out var newPlan) && newPlan is string planCode && !values.ContainsKey("mrr_h"))
            values["mrr_h"] = await PlanPrice(control, planCode, (values.GetValueOrDefault("billing_cycle") as string) ?? (r["billing_cycle"] as string), ct);
        await using (var c = await control.OpenAsync(ct))
            await control.Query(c).Query("control.tenant").Where("tenant_id", id).UpdateAsync(values, cancellationToken: ct);
        control.Forget(); metrics.Forget(id);
        var changed = string.Join(", ", values.Keys.Where(k => k != "updated_at_utc"));
        var audit = await Record(control, id, "client.update", "Updated client", null, changed, null, user, http, ct);
        return Results.Ok(new { ok = true, audit });
    }

    /// <summary>
    /// The console's "Add client": the client and its address, its database (tested), every table prepared, the
    /// business with its first branch and the owner account, in one go. Each step that fails says which.
    /// </summary>
    private static async Task<IResult> CreateAsync(JsonObject body, ControlStore control, ClientMetrics metrics, IAuthStore store, IDb db,
        IConfiguration config, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        string S(string k) => body[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        var name = S("companyName");
        var slug = S("slug").ToLowerInvariant();
        var dbNode = body["db"] as JsonObject ?? new JsonObject();
        /* "Create a database for this client": a new empty one named after the client's GUID, on the control database's server. */
        var clientGuid = Guid.NewGuid().ToString("N");
        if (body["createDatabase"] is JsonValue cdv && cdv.TryGetValue<bool>(out var createDb) && createDb)
        {
            if (string.IsNullOrWhiteSpace(S("companyName"))) return Error(422, "name_required", "A company name is required.", "اسم الشركة مطلوب.");
            var (dbName, dbError) = await PosApi.Observability.ObservabilityEndpoints.CreateClientDatabaseAsync(control, $"ayn_{clientGuid}", ct);
            if (dbError is not null) return Error(422, "db_create_failed", dbError, "تعذر إنشاء قاعدة البيانات.");
            var login = control.ServerLogin();
            dbNode = new JsonObject { ["host"] = login.Host, ["port"] = login.Port, ["database"] = dbName, ["user"] = login.User, ["password"] = login.Password };
            await Record(control, null, "database.create", "Created database", null, dbName, S("companyName"), user, http, ct);
        }
        string D(string k) => dbNode[k]?.ToString()?.Trim() ?? "";
        var dbBody = new SuperAdminEndpoints.DbBody(D("host"), int.TryParse(D("port"), out var port) ? port : 5432, D("database"), D("user"), D("password"));
        if (string.IsNullOrWhiteSpace(name)) return Error(422, "name_required", "A company name is required.", "اسم الشركة مطلوب.");
        if (string.IsNullOrWhiteSpace(S("adminEmail"))) return Error(422, "admin_required", "The admin needs an email.", "بريد المسؤول مطلوب.");

        /* 1. The client row (validates slug and tests the database). */
        var plan = S("plan") is { Length: > 0 } p ? p : "Basic";
        var trial = body["trial"] is JsonValue tv && tv.TryGetValue<bool>(out var tb) && tb;
        var created = await SuperAdminEndpoints.CreateClientRowAsync(new SuperAdminEndpoints.CreateBody(slug, name, null, plan, dbBody,
            S("adminName"), S("adminEmail"), S("adminPhone"), null), control, user, ct);
        if (created.Error is not null) return created.Error;
        var id = created.Id!.Value;

        /* Commercial profile. */
        var cycle = S("cycle") is { Length: > 0 } cy ? cy : "monthly";
        var starts = DateTime.TryParse(S("startsOn"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var st) ? st : DateTime.UtcNow;
        var trialDays = await TrialDays(control, ct);
        var ends = trial ? starts.AddDays(trialDays) : cycle == "annual" ? starts.AddYears(1) : starts.AddMonths(1);
        var methods = body["paymentMethods"] is JsonArray arr ? string.Join(",", arr.Select(x => x!.ToString())) : "Cash,Mada";
        await using (var c = await control.OpenAsync(ct))
        {
            await control.Query(c).Query("control.tenant").Where("tenant_id", id).UpdateAsync(new Dictionary<string, object?>
            {
                ["name_ar"] = S("companyNameAr") is { Length: > 0 } ar ? ar : name, ["business_type"] = S("businessType") is { Length: > 0 } bt ? bt : S("industry"),
                ["city"] = S("city"), ["plan_code"] = trial ? "Trial" : plan, ["billing_cycle"] = cycle, ["is_trial"] = trial, ["trial_days"] = trial ? trialDays : null,
                ["subscription_starts"] = starts, ["subscription_ends"] = ends, ["mrr_h"] = trial ? 0 : await PlanPrice(control, plan, cycle, ct),
                ["poc_username"] = S("adminUsername"), ["poc_role"] = "Owner", ["payment_methods"] = methods,
                ["vat_rate"] = body["defaultVat"] is JsonValue vv && vv.TryGetValue<int>(out var vat) ? vat : 15,
                ["prices_include_vat"] = !(body["pricesIncludeVat"] is JsonValue pv && pv.TryGetValue<bool>(out var inc) && !inc),
                ["cr_number"] = S("cr"), ["vat_number"] = S("vat"), ["registered_by"] = user.Username, ["updated_at_utc"] = DateTime.UtcNow,
                ["client_guid"] = clientGuid, ["notes"] = S("notes") is { Length: > 0 } nt ? nt : null,
            }, cancellationToken: ct);
        }

        /* 2. Prepare the database. */
        var row = (await Find(control, id, ct))!;
        var tenant = control.ToTenant(row);
        if (!SchemaMigrator.Migrate(config, tenant.ConnectionString, SchemaMigrator.ClientNamespace, $"client {tenant.Slug}"))
        {
            await Record(control, id, "client.migrate", "Database preparation failed", null, "failed", null, user, http, ct, "failed");
            return Error(422, "migration_failed", "The client was created, but its database could not be prepared. Open the client and press Update database.", "أُنشئ العميل لكن تعذر تجهيز قاعدة البيانات.");
        }
        await SchemaMigrator.MarkMigrated(control, id);

        /* 3. Business, first branch, owner (and the standard roles). */
        var owner = await SuperAdminEndpoints.CreateOwnerAsync(control, store, db, user, id, new SuperAdminEndpoints.OwnerBody(
            S("companyNameAr") is { Length: > 0 } nar ? nar : name, name, S("city"), S("cr"), S("vat"), S("adminName"), S("adminEmail"), S("adminPhone")), ct);
        if (owner.Error is not null) return owner.Error;
        if (S("branchName") is { Length: > 0 } branchName)
        {
            TenantContext.Current = tenant;
            try
            {
                await using var conn = await db.OpenAsync(ct);
                await db.Query(conn).Query("core.branch").Where("branch_id", owner.BranchId).UpdateAsync(new Dictionary<string, object?> { ["name_en"] = branchName, ["name_ar"] = branchName }, cancellationToken: ct);
            }
            finally { TenantContext.Current = null; }
        }

        control.Forget(); metrics.Forget(id);
        var audit = await Record(control, id, "client.create", "Created client", null, "created", null, user, http, ct);
        var fresh = await Find(control, id, ct);
        var f = await metrics.ForAsync(control.ToTenant(fresh!), fresh: true, ct);
        return Results.Ok(new { ok = true, audit, client = Row(fresh!, f, SchemaMigrator.LatestClientVersion(), control) });
    }

    private static async Task<IResult> RenewAsync(long id, RenewBody body, ControlStore control, ClientMetrics metrics, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var r = await Find(control, id, ct);
        if (r is null) return NotFound();
        var days = Math.Clamp(body.Days ?? 360, 1, 3650);
        var from = Dt(r["subscription_ends"]) is DateTime e && e > DateTime.UtcNow ? e : DateTime.UtcNow;
        var ends = from.AddDays(days);
        var wasTrial = (bool)(r["is_trial"] ?? false);
        var values = new Dictionary<string, object?>
        {
            ["subscription_ends"] = ends, ["renewals"] = Convert.ToInt32(r["renewals"] ?? 0) + 1, ["last_renewed_at_utc"] = DateTime.UtcNow,
            ["is_trial"] = false, ["updated_at_utc"] = DateTime.UtcNow,
        };
        if ((string)r["status"]! == "suspended") values["status"] = "active";
        if (wasTrial && (r["plan_code"] as string) == "Trial") { values["plan_code"] = "Basic"; values["mrr_h"] = await PlanPrice(control, "Basic", "monthly", ct); }
        await using (var c = await control.OpenAsync(ct))
            await control.Query(c).Query("control.tenant").Where("tenant_id", id).UpdateAsync(values, cancellationToken: ct);
        control.Forget(); metrics.Forget(id);
        var audit = await Record(control, id, "subscription.renew", "Renewed subscription", Dt(r["subscription_ends"])?.ToString("yyyy-MM-dd"), ends.ToString("yyyy-MM-dd"), $"{days} days", user, http, ct);
        return Results.Ok(new { ok = true, audit });
    }

    private static async Task<IResult> CancelAsync(long id, SuspendBody body, ControlStore control, ClientMetrics metrics, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var r = await Find(control, id, ct);
        if (r is null) return NotFound();
        if (string.IsNullOrWhiteSpace(body.Reason)) return Error(422, "reason_required", "Give a reason for cancelling.", "اذكر سبب الإلغاء.");
        await using (var c = await control.OpenAsync(ct))
            await control.Query(c).Query("control.tenant").Where("tenant_id", id).UpdateAsync(new Dictionary<string, object?>
                { ["subscription_ends"] = DateTime.UtcNow, ["status_reason"] = body.Reason, ["updated_at_utc"] = DateTime.UtcNow }, cancellationToken: ct);
        control.Forget(); metrics.Forget(id);
        var audit = await Record(control, id, "subscription.cancel", "Cancelled subscription", "active", "cancelled", body.Reason, user, http, ct);
        return Results.Ok(new { ok = true, audit });
    }

    /* ------------------------------------------------------------------ lists */

    private static async Task<IResult> SubscriptionsAsync(ControlStore control, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var rows = (await control.Query(c).Query("control.tenant").OrderBy("tenant_id").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        var list = new List<object>();
        foreach (var r in rows) list.Add(await Subscription(control, r, ct));
        return Results.Ok(list);
    }

    private static async Task<IResult> ExpiringAsync(ControlStore control, ClientMetrics metrics, int? days, CancellationToken ct)
    {
        var within = Math.Clamp(days ?? 60, 1, 3650);
        var data = await LoadAsync(control, metrics, false, ct);
        var list = new List<JsonObject>();
        foreach (var r in data.Rows)
        {
            var row = Row(r, data.Figures.GetValueOrDefault(Convert.ToInt64(r["tenant_id"])), data.Latest, control);
            if ((int)row["daysToRenewal"]! > within) continue;
            var sub = JsonSerializer.SerializeToNode(await Subscription(control, r, ct), Json)!.AsObject();
            sub["businessType"] = row["businessType"]?.DeepClone(); sub["poc"] = row["poc"]!.DeepClone(); sub["balanceH"] = row["balanceH"]!.DeepClone();
            sub["users"] = row["users"]!.DeepClone(); sub["devices"] = 0; sub["renewals"] = row["renewals"]!.DeepClone();
            sub["lastRenewedOn"] = row["lastRenewedOn"]?.DeepClone(); sub["clientStatus"] = row["status"]!.DeepClone();
            list.Add(sub);
        }
        return Results.Ok(list.OrderBy(x => (int)x["daysToRenewal"]!));
    }

    private static async Task<IResult> PlansAsync(ControlStore control, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var rows = await control.Query(c).Query("control.plan").Where("is_active", true).OrderBy("sort").GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Cast<IDictionary<string, object?>>().Select(PlanShape));
    }

    private static JsonObject PlanShape(IDictionary<string, object?> p)
    {
        var o = new JsonObject
        {
            ["key"] = (string)p["code"]!, ["label"] = (string)p["name"]!,
            ["monthlyH"] = p["monthly_h"] is null ? null : L(p["monthly_h"]), ["annualH"] = p["annual_h"] is null ? null : L(p["annual_h"]),
            ["isTrial"] = (bool)(p["is_trial"] ?? false), ["trialDays"] = p["trial_days"] is null ? null : Convert.ToInt32(p["trial_days"]),
            ["features"] = JsonNode.Parse((string)p["features_json"]!),
        };
        foreach (var (k, v) in JsonNode.Parse((string)p["limits_json"]!)!.AsObject()) o[k] = v?.DeepClone();
        return o;
    }

    private static readonly Dictionary<string, string> Lists = new()
    {
        ["cities"] = "city", ["services"] = "service", ["payment-methods"] = "payment_method", ["deactivation-reasons"] = "deactivation_reason",
    };

    private static async Task<IResult> MasterAsync(string list, ControlStore control, CancellationToken ct)
    {
        if (!Lists.TryGetValue(list, out var key)) return Error(404, "not_found", "Unknown list.", "قائمة غير معروفة.");
        await using var c = await control.OpenAsync(ct);
        var rows = await control.Query(c).Query("control.master_item").Where("list_key", key).Where("is_active", true).OrderBy("sort").GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Cast<IDictionary<string, object?>>().Select(m =>
        {
            var o = new JsonObject { ["value"] = (string)m["code"]!, ["label"] = (string)m["name_en"]!, ["labelAr"] = m["name_ar"] as string };
            if (m["extra_json"] is string x) foreach (var (k, v) in JsonNode.Parse(x)!.AsObject()) o[k] = v?.DeepClone();
            return o;
        }));
    }

    private static async Task<IResult> AuditRowsAsync(ControlStore control, long? clientId, int take, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var names = await Names(control, c, ct);
        var q = control.Query(c).Query("control.tenant_event").OrderByDesc("event_id").Limit(take);
        if (clientId is not null) q = q.Where("tenant_id", clientId);
        var rows = await q.GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Cast<IDictionary<string, object?>>().Select(e =>
        {
            var tid = e["tenant_id"] is null ? (long?)null : L(e["tenant_id"]);
            var kind = (string)e["kind"]!;
            return new
            {
                id = $"AUD-{L(e["event_id"])}", at = Dt(e["at_utc"]), admin = (e["actor"] as string) ?? "system", action = kind,
                label = (e["label"] as string) ?? Pretty(kind), entity = kind.Split('.')[0],
                clientId = tid?.ToString(CultureInfo.InvariantCulture), clientName = tid is null ? "POSSA" : names.GetValueOrDefault(tid.Value, $"#{tid}"),
                before = e["before_value"] as string, after = (e["after_value"] as string) ?? (e["detail"] as string), reason = e["reason"] as string,
                detail = e["detail"] as string, method = (string?)null, endpoint = (string?)null, table = (string?)null, operation = (string?)null,
                durationMs = (int?)null, rowsAffected = (int?)null, ip = e["ip"] as string,
                result = (e["result"] as string) ?? (kind.Contains("fail") ? "failed" : "success"),
            };
        }));
    }

    private static async Task<IResult> NotificationsAsync(ControlStore control, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var rows = await control.Query(c).Query("control.notification").OrderByDesc("notification_id").Limit(100).GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Cast<IDictionary<string, object?>>().Select(n => new
        {
            id = L(n["notification_id"]).ToString(CultureInfo.InvariantCulture), kind = (string)n["kind"]!, title = (string)n["title"]!, body = n["body"] as string,
            severity = (string)n["severity"]!, read = (bool)(n["is_read"] ?? false), at = Dt(n["at_utc"]),
            clientId = n["tenant_id"] is null ? null : Convert.ToString(n["tenant_id"], CultureInfo.InvariantCulture),
        }));
    }

    private static async Task<IResult> SearchAsync(string? q, ControlStore control, ClientMetrics metrics, IDb db, CancellationToken ct)
    {
        var needle = (q ?? "").Trim().ToLowerInvariant();
        if (needle.Length < 2) return Results.Ok(Array.Empty<object>());
        await using var c = await control.OpenAsync(ct);
        var rows = (await control.Query(c).Query("control.tenant").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
        bool Hit(object? v) => v?.ToString()?.ToLowerInvariant().Contains(needle) == true;
        var hits = rows.Where(r => Hit(r["name"]) || Hit(r["slug"]) || Hit(r["name_ar"]) || Hit(r["contact_email"]) || Hit(r["cr_number"]) || Hit(r["vat_number"]))
            .Take(8).Select(r => (object)new
            {
                kind = "client", id = Convert.ToString(r["tenant_id"], CultureInfo.InvariantCulture), title = (string)r["name"]!,
                subtitle = $"{(r["plan_code"] as string) ?? "—"} · {(r["city"] as string) ?? "—"} · {r["slug"]}.{control.Options.RootDomain}",
                to = $"/superadmin/clients/{r["tenant_id"]}",
            }).ToList();
        var audits = (await control.Query(c).Query("control.tenant_event").WhereLike("label", $"%{needle}%").OrderByDesc("event_id").Limit(3).GetAsync(cancellationToken: ct))
            .Cast<IDictionary<string, object?>>().Select(e => (object)new { kind = "audit", id = $"AUD-{L(e["event_id"])}", title = (e["label"] as string) ?? (string)e["kind"]!, subtitle = Dt(e["at_utc"])?.ToString("u"), to = "/superadmin/audit" });
        return Results.Ok(hits.Concat(audits));
    }

    private static async Task<IResult> PlatformHealthAsync(ControlStore control, ClientMetrics metrics, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool controlOk;
        try { await using var c = await control.OpenAsync(ct); controlOk = true; } catch { controlOk = false; }
        var controlMs = watch.ElapsedMilliseconds;
        var data = controlOk ? await LoadAsync(control, metrics, false, ct) : new Loaded([], [], 0);
        var f = data.Figures.Values.ToList();
        var reachable = f.Count(x => x.Reachable);
        var requests = f.Sum(x => x.Requests24h);
        var errors = f.Sum(x => x.ServerErrors24h);
        var errorPct = requests == 0 ? 0 : Math.Round(errors * 100.0 / requests, 2);
        var dbMs = f.Where(x => x.Reachable).Select(x => x.Milliseconds).DefaultIfEmpty(0).Average();
        var behind = data.Rows.Count(r => r["schema_version"] is null || L(r["schema_version"]) < data.Latest);
        string State(bool ok, bool degraded = false) => !ok ? "down" : degraded ? "degraded" : "operational";
        return Results.Ok(new
        {
            services = new object[]
            {
                new { key = "api", label = "API", status = "operational", uptime = (double?)null, latencyMs = (double?)null, errorRate = errorPct },
                new { key = "database", label = "Control database", status = State(controlOk), uptime = (double?)null, latencyMs = (double?)controlMs, errorRate = 0.0 },
                new { key = "clients", label = "Client databases", status = State(f.Count == 0 || reachable > 0, reachable < f.Count), uptime = f.Count == 0 ? 100 : Math.Round(reachable * 100.0 / f.Count, 2), latencyMs = (double?)Math.Round(dbMs), errorRate = errorPct },
                new { key = "migrations", label = "Database versions", status = State(true, behind > 0), uptime = (double?)null, latencyMs = (double?)null, errorRate = (double)behind },
            },
            metrics = new
            {
                apiResponseMs = (double?)null, apiResponsePreviousMs = (double?)null, errorRatePct = errorPct, errorRatePreviousPct = (double?)null,
                failedJobs = behind, failedJobsPrevious = (int?)null, lastBackupAt = (DateTime?)null,
                clientsReachable = reachable, clients = f.Count, serverErrors24h = errors, requests24h = requests,
            },
        });
    }

    /* ------------------------------------------------------------------ helpers */

    private static async Task<IResult> InClient(ControlStore control, long id, CancellationToken ct, Func<TenantInfo, System.Data.Common.DbConnection, Task<List<object>>> work, IDb db)
    {
        var r = await Find(control, id, ct);
        if (r is null) return NotFound();
        var tenant = control.ToTenant(r);
        TenantContext.Current = tenant;
        try
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await work(tenant, conn));
        }
        catch (Exception ex)
        {
            return Error(502, "client_unreachable", $"Could not read this client's database: {ex.Message}", "تعذر قراءة قاعدة بيانات العميل.");
        }
        finally { TenantContext.Current = null; }
    }

    internal static async Task<object> Record(ControlStore control, long? tenantId, string action, string label, string? before, string? after, string? reason,
        ICurrentUser user, HttpContext http, CancellationToken ct, string result = "success")
    {
        var at = DateTime.UtcNow;
        var ip = http.Connection.RemoteIpAddress?.ToString();
        await using var c = await control.OpenAsync(ct);
        var id = await control.Query(c).Query("control.tenant_event").InsertGetIdAsync<long>(new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["kind"] = action, ["label"] = label, ["before_value"] = before, ["after_value"] = after, ["reason"] = reason,
            ["actor"] = user.Username, ["result"] = result, ["ip"] = ip, ["at_utc"] = at, ["detail"] = reason,
        }, cancellationToken: ct);
        return new { id = $"AUD-{id}", at, admin = user.Username, action, label, clientId = tenantId?.ToString(CultureInfo.InvariantCulture), before, after, reason, result };
    }

    private static async Task<Dictionary<long, string>> Names(ControlStore control, System.Data.Common.DbConnection c, CancellationToken ct) =>
        (await control.Query(c).Query("control.tenant").Select("tenant_id", "name").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>()
            .ToDictionary(r => L(r["tenant_id"]), r => (string)r["name"]!);

    private static async Task<IDictionary<string, object?>?> Find(ControlStore control, long id, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        return (await control.Query(c).Query("control.tenant").Where("tenant_id", id).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();
    }

    private static async Task<Dictionary<string, double?>> PlanLimits(ControlStore control, string plan, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var row = (await control.Query(c).Query("control.plan").Where("code", plan).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();
        if (row is null) return new();
        return JsonNode.Parse((string)row["limits_json"]!)!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value is null ? (double?)null : kv.Value.GetValue<double>());
    }

    private static async Task<long> PlanPrice(ControlStore control, string plan, string? cycle, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var row = (await control.Query(c).Query("control.plan").Where("code", plan).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();
        if (row is null) return 0;
        /* MRR: annual plans spread over twelve months. */
        return cycle == "annual" && row["annual_h"] is not null ? L(row["annual_h"]) / 12 : row["monthly_h"] is null ? 0 : L(row["monthly_h"]);
    }

    private static async Task<int> TrialDays(ControlStore control, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var row = (await control.Query(c).Query("control.plan").Where("is_trial", true).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();
        return row?["trial_days"] is null ? 15 : Convert.ToInt32(row["trial_days"]);
    }

    private static string Pretty(string kind) => string.IsNullOrEmpty(kind) ? kind : char.ToUpperInvariant(kind[0]) + kind[1..].Replace('_', ' ').Replace('.', ' ');
    private static long L(object? v) => v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    private static DateTime? Dt(object? v) => v is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    private static IResult NotFound() => Error(404, "not_found", "That client does not exist.", "هذا العميل غير موجود.");
    private static IResult Error(int status, string code, string en, string ar) =>
        Results.Json(new { code, message = en, messageEn = en, messageAr = ar }, statusCode: status);
}
