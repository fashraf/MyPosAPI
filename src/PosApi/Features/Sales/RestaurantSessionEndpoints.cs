using System.Data.Common;
using System.Text.Json.Nodes;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;
using PosApi.Features.Catalog;
using SqlKata.Execution;

namespace PosApi.Features.Sales;

public sealed record OpenSessionRequest(
    string BranchId, string? RegisterId, long OpeningBalanceH, string? OpeningNotes, string? Notes,
    DateOnly? BusinessDate, string? OpenedBy, string? IdempotencyKey);
public sealed record CloseSessionRequest(
    long ActualClosingH, string? ClosingNotes, string? DiscrepancyReason, string? ClosedBy, JsonObject? Summary);
public sealed record CashMovementRequest(string Type, long AmountH, string Description, string? CreatedBy);
public sealed record ReopenRequest(string Reason, string? ReopenedBy);

/// <summary>
/// Restaurant business sessions (the trading day): open, move cash, close, reopen, history.
///
/// Rules held here, for every client: one open session per till (unique open_key in the database), no sale without
/// an open session (checked in the sale transaction), session numbers from the number series (1001, 1002 …), the
/// expected cash computed on the server from its own sales and movements, and a reason required for any difference.
/// </summary>
public static class RestaurantSessionEndpoints
{
    private const string View = "restaurant.session.view";
    private static readonly HashSet<string> MovementTypes = new() { "addition", "expense", "withdrawal", "transfer" };

    public static void MapRestaurantSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/restaurant-sessions")
            .WithTags("Restaurant sessions")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        g.MapGet("/state", StateAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapGet("/status", StatusAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapGet("/current", CurrentAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapGet("/history", HistoryAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.view_history"));
        g.MapGet("/{id}", GetAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapGet("/{id}/cash-movements", MovementsAsync).RequireAuthorization(PermissionPolicy.For("restaurant.cash_movement.view"));
        g.MapGet("/{id}/audit-log", AuditAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.view_history"));
        g.MapGet("/{id}/closing-preview", PreviewAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.reconcile"));
        g.MapPost("/open", OpenAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.open"));
        g.MapPost("/{id}/cash-movements", AddMovementAsync).RequireAuthorization(PermissionPolicy.For("restaurant.cash_movement.create"));
        g.MapPost("/{id}/close", CloseAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.close"));
        g.MapPost("/{id}/reopen", ReopenAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.reopen"));
    }

    /* ---------------------------------------------------------------- reads */

    /// <summary>Everything a till needs to show the day: recent sessions with their movements and audit trail.</summary>
    private static async Task<IResult> StateAsync(IDb db, string? branchId, int days = 45, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var q = db.Query(conn);
        var since = DateTime.UtcNow.Date.AddDays(-Math.Clamp(days, 1, 400));
        var s = q.Query("sales.restaurant_session").Where(w => w.Where("opened_at_utc", ">=", since).OrWhere("status", "open"));
        if (!string.IsNullOrWhiteSpace(branchId)) s.Where("branch_id", branchId);
        var sessions = (await s.OrderByDesc("opened_at_utc").GetAsync(cancellationToken: ct)).Select(Row).ToList();
        var ids = sessions.Select(x => (string)x["session_id"]!).ToArray();

        var movements = ids.Length == 0 ? new List<JsonObject>() : (await q.Query("sales.session_movement").WhereIn("session_id", ids)
            .OrderBy("created_at_utc").GetAsync(cancellationToken: ct)).Select(Row).Select(MovementDoc).ToList();
        var audit = ids.Length == 0 ? new List<JsonObject>() : (await q.Query("sales.session_audit").WhereIn("session_id", ids)
            .OrderBy("created_at_utc").GetAsync(cancellationToken: ct)).Select(Row).Select(AuditDoc).ToList();

        return Results.Ok(new { sessions = sessions.Select(SessionDoc).ToList(), movements, audit, serverTimeUtc = DateTime.UtcNow });
    }

    private static async Task<IResult> StatusAsync(IDb db, string? branchId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var open = await OpenSession(db.Query(conn), branchId, null, null);
        return Results.Ok(new { open = open is not null, session = open is null ? null : SessionDoc(open) });
    }

    private static async Task<IResult> CurrentAsync(IDb db, string? branchId, bool? includeClosed, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var x = db.Query(conn).Query("sales.restaurant_session");
        if (!string.IsNullOrWhiteSpace(branchId)) x.Where("branch_id", branchId);
        if (includeClosed != true) x.Where("status", "open");
        object? row = await x.OrderByDesc("opened_at_utc").FirstOrDefaultAsync(cancellationToken: ct);
        return Results.Ok(row is null ? null : SessionDoc(Row(row)));
    }

    private static async Task<IResult> HistoryAsync(IDb db, string? branchId, int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        var paging = new PageQuery { Page = page <= 0 ? 1 : page, PageSize = pageSize };
        await using var conn = await db.OpenAsync(ct);
        SqlKata.Query Base()
        {
            var x = db.Query(conn).Query("sales.restaurant_session");
            if (!string.IsNullOrWhiteSpace(branchId)) x.Where("branch_id", branchId);
            return x;
        }
        var total = await Base().CountAsync<int>(cancellationToken: ct);
        var rows = await Base().OrderByDesc("opened_at_utc").Offset(paging.Offset).Limit(paging.PageSize).GetAsync(cancellationToken: ct);
        return Results.Ok(new PagedResult<JsonObject>
        {
            Items = rows.Select(Row).Select(SessionDoc).ToList(), Page = paging.Page, PageSize = paging.PageSize, TotalCount = total,
        });
    }

    private static async Task<IResult> GetAsync(string id, IDb db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        object? row = await db.Query(conn).Query("sales.restaurant_session").Where("session_id", id).FirstOrDefaultAsync(cancellationToken: ct);
        return row is null ? NotFound() : Results.Ok(SessionDoc(Row(row)));
    }

    private static async Task<IResult> MovementsAsync(string id, IDb db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await db.Query(conn).Query("sales.session_movement").Where("session_id", id).OrderBy("created_at_utc").GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Select(Row).Select(MovementDoc));
    }

    private static async Task<IResult> AuditAsync(string id, IDb db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await db.Query(conn).Query("sales.session_audit").Where("session_id", id).OrderBy("created_at_utc").GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Select(Row).Select(AuditDoc));
    }

    /// <summary>The expected cash in the drawer right now, worked out on the server.</summary>
    private static async Task<IResult> PreviewAsync(string id, IDb db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        object? row = await db.Query(conn).Query("sales.restaurant_session").Where("session_id", id).FirstOrDefaultAsync(cancellationToken: ct);
        if (row is null) return NotFound();
        return Results.Ok(await CashBreakdown(db.Query(conn), Row(row), null));
    }

    /* --------------------------------------------------------------- writes */

    private static async Task<IResult> OpenAsync(OpenSessionRequest body, IDb db, INumberSeries numbers, ICurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.BranchId))
            return Error(422, "branch_required", "Choose the branch to open.", "اختر الفرع المراد فتحه.");
        if (body.OpeningBalanceH < 0)
            return Error(422, "bad_opening", "The opening cash cannot be negative.", "لا يمكن أن يكون النقد الافتتاحي سالبًا.");
        if (!user.CanSeeBranch(body.BranchId))
            return Error(403, "branch_forbidden", "You do not have access to this branch.", "ليست لديك صلاحية على هذا الفرع.");

        var register = string.IsNullOrWhiteSpace(body.RegisterId) ? "reg_main" : body.RegisterId!;
        var actor = string.IsNullOrWhiteSpace(body.OpenedBy) ? user.Username : body.OpenedBy!;
        var now = DateTime.UtcNow;
        try
        {
            var doc = await db.InTransactionAsync(async (conn, tx) =>
            {
                var q = db.Query(conn);
                if (await OpenSession(q, body.BranchId, register, tx) is { } already)
                    throw new ConflictException(SessionDoc(already));

                var no = await numbers.NextAsync(conn, tx, "session");
                var id = $"rss_{body.BranchId}_{no}";
                var businessDate = body.BusinessDate ?? DateOnly.FromDateTime(RiyadhBusinessDate(now));
                var notes = body.OpeningNotes ?? body.Notes ?? "";
                await q.Query("sales.restaurant_session").InsertAsync(new Dictionary<string, object?>
                {
                    ["session_id"] = id, ["session_no"] = no, ["branch_id"] = body.BranchId, ["register_id"] = register,
                    ["business_date"] = businessDate.ToDateTime(TimeOnly.MinValue), ["status"] = "open",
                    ["open_key"] = $"{body.BranchId}|{register}", ["opened_at_utc"] = now, ["opened_by"] = actor,
                    ["opened_by_id"] = user.UserId, ["opening_balance_h"] = body.OpeningBalanceH, ["opening_notes"] = notes,
                    ["last_order_no"] = 0, ["reopen_count"] = 0, ["created_at_utc"] = now, ["updated_at_utc"] = now,
                }, tx);
                await Audit(q, tx, id, "opened", $"Restaurant opened with SAR {body.OpeningBalanceH / 100m:0.00} float", null, body.OpeningBalanceH.ToString(), actor, now);
                return SessionDoc(Row((await q.Query("sales.restaurant_session").Where("session_id", id).FirstAsync(tx))));
            }, ct);
            return Results.Ok(doc);
        }
        catch (ConflictException c)
        {
            return Results.Json(new { code = "already_open", messageEn = "The restaurant is already open on this till.",
                messageAr = "المطعم مفتوح بالفعل على هذا الجهاز.", session = c.Session }, statusCode: 409);
        }
    }

    private static async Task<IResult> AddMovementAsync(string id, CashMovementRequest body, IDb db, ICurrentUser user, CancellationToken ct)
    {
        if (!MovementTypes.Contains(body.Type))
            return Error(422, "bad_type", "Unknown cash movement type.", "نوع حركة نقدية غير معروف.");
        if (body.AmountH <= 0)
            return Error(422, "bad_amount", "Enter an amount above zero.", "أدخل مبلغًا أكبر من صفر.");
        if (string.IsNullOrWhiteSpace(body.Description))
            return Error(422, "description_required", "Say what the cash was for.", "اذكر سبب الحركة النقدية.");

        var actor = string.IsNullOrWhiteSpace(body.CreatedBy) ? user.Username : body.CreatedBy!;
        var now = DateTime.UtcNow;
        var result = await db.InTransactionAsync<object?>(async (conn, tx) =>
        {
            var q = db.Query(conn);
            var s = await q.Query("sales.restaurant_session").Where("session_id", id).FirstOrDefaultAsync(tx);
            if (s is null) return null;
            if ((string)((IDictionary<string, object?>)s)["status"]! != "open") return "closed";
            var mid = $"rcm_{Guid.NewGuid():N}"[..20];
            await q.Query("sales.session_movement").InsertAsync(new Dictionary<string, object?>
            {
                ["movement_id"] = mid, ["session_id"] = id, ["movement_type"] = body.Type, ["amount_h"] = body.AmountH,
                ["description"] = body.Description.Trim(), ["user_name"] = actor, ["created_at_utc"] = now,
            }, tx);
            await Audit(q, tx, id, "cash_movement", $"{body.Type}: SAR {body.AmountH / 100m:0.00} — {body.Description.Trim()}", null, body.AmountH.ToString(), actor, now);
            return MovementDoc(Row(await q.Query("sales.session_movement").Where("movement_id", mid).FirstAsync(tx)));
        }, ct);

        return result switch
        {
            null => NotFound(),
            "closed" => Error(409, "session_closed", "This day is already closed.", "تم إغلاق هذا اليوم بالفعل."),
            _ => Results.Ok(result),
        };
    }

    private static async Task<IResult> CloseAsync(string id, CloseSessionRequest body, IDb db, ICurrentUser user, CancellationToken ct)
    {
        if (body.ActualClosingH < 0)
            return Error(422, "bad_count", "The counted cash cannot be negative.", "لا يمكن أن يكون النقد المعدود سالبًا.");
        var actor = string.IsNullOrWhiteSpace(body.ClosedBy) ? user.Username : body.ClosedBy!;
        var now = DateTime.UtcNow;

        var outcome = await db.InTransactionAsync<object?>(async (conn, tx) =>
        {
            var q = db.Query(conn);
            object? raw = await q.Query("sales.restaurant_session").Where("session_id", id).FirstOrDefaultAsync(tx);
            if (raw is null) return null;
            var s = Row(raw);
            if ((string)s["status"]! != "open") return "closed";

            var cash = await CashBreakdown(q, s, tx);
            var expected = (long)cash["expectedH"]!;
            var diff = body.ActualClosingH - expected;
            if (diff != 0 && string.IsNullOrWhiteSpace(body.DiscrepancyReason)) return ("reason", expected, diff);

            var details = CatalogEndpoints.Parse(s["details_json"]) ?? new JsonObject();
            if (body.Summary is not null) details["summary"] = body.Summary.DeepClone();
            details["cashServer"] = cash;
            await q.Query("sales.restaurant_session").Where("session_id", id).UpdateAsync(new Dictionary<string, object?>
            {
                ["status"] = "closed", ["open_key"] = id, ["closed_at_utc"] = now, ["closed_by"] = actor, ["closed_by_id"] = user.UserId,
                ["expected_cash_h"] = expected, ["actual_closing_h"] = body.ActualClosingH, ["discrepancy_h"] = diff,
                ["discrepancy_reason"] = body.DiscrepancyReason, ["closing_notes"] = body.ClosingNotes,
                ["details_json"] = details.ToJsonString(), ["updated_at_utc"] = now,
            }, tx);
            await Audit(q, tx, id, "closed",
                $"Restaurant closed. Expected SAR {expected / 100m:0.00}, counted SAR {body.ActualClosingH / 100m:0.00}, difference SAR {diff / 100m:0.00}",
                expected.ToString(), body.ActualClosingH.ToString(), actor, now);
            return SessionDoc(Row(await q.Query("sales.restaurant_session").Where("session_id", id).FirstAsync(tx)));
        }, ct);

        return outcome switch
        {
            null => NotFound(),
            "closed" => Error(409, "session_closed", "This day is already closed.", "تم إغلاق هذا اليوم بالفعل."),
            ValueTuple<string, long, long> r => Results.Json(new
            {
                code = "reason_required", expectedH = r.Item2, differenceH = r.Item3,
                messageEn = "The counted cash does not match. Choose a reason for the difference.",
                messageAr = "النقد المعدود لا يطابق المتوقع. اختر سبب الفرق.",
            }, statusCode: 422),
            _ => Results.Ok(outcome),
        };
    }

    private static async Task<IResult> ReopenAsync(string id, ReopenRequest body, IDb db, ICurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Reason))
            return Error(422, "reason_required", "Say why the day is being reopened.", "اذكر سبب إعادة فتح اليوم.");
        var actor = string.IsNullOrWhiteSpace(body.ReopenedBy) ? user.Username : body.ReopenedBy!;
        var now = DateTime.UtcNow;
        try
        {
            var outcome = await db.InTransactionAsync<object?>(async (conn, tx) =>
            {
                var q = db.Query(conn);
                object? raw = await q.Query("sales.restaurant_session").Where("session_id", id).FirstOrDefaultAsync(tx);
                if (raw is null) return null;
                var s = Row(raw);
                if ((string)s["status"]! == "open") return "open";
                if (await OpenSession(q, (string)s["branch_id"]!, (string)s["register_id"]!, tx) is { } other)
                    throw new ConflictException(SessionDoc(other));
                await q.Query("sales.restaurant_session").Where("session_id", id).UpdateAsync(new Dictionary<string, object?>
                {
                    ["status"] = "open", ["open_key"] = $"{s["branch_id"]}|{s["register_id"]}", ["closed_at_utc"] = null,
                    ["reopen_count"] = Convert.ToInt32(s["reopen_count"]) + 1, ["updated_at_utc"] = now,
                }, tx);
                await Audit(q, tx, id, "reopened", $"Reopened: {body.Reason.Trim()}", "closed", "open", actor, now);
                return SessionDoc(Row(await q.Query("sales.restaurant_session").Where("session_id", id).FirstAsync(tx)));
            }, ct);
            return outcome switch
            {
                null => NotFound(),
                "open" => Error(409, "already_open", "This day is already open.", "هذا اليوم مفتوح بالفعل."),
                _ => Results.Ok(outcome),
            };
        }
        catch (ConflictException)
        {
            return Error(409, "another_open", "Another day is open on this till. Close it first.", "يوجد يوم آخر مفتوح على هذا الجهاز. أغلقه أولًا.");
        }
    }

    /* -------------------------------------------------------------- helpers */

    internal static async Task<IDictionary<string, object?>?> OpenSession(QueryFactory q, string? branchId, string? registerId, DbTransaction? tx)
    {
        var x = q.Query("sales.restaurant_session").Where("status", "open");
        if (!string.IsNullOrWhiteSpace(branchId)) x.Where("branch_id", branchId);
        if (!string.IsNullOrWhiteSpace(registerId)) x.Where("register_id", registerId);
        object? row = await x.OrderByDesc("opened_at_utc").FirstOrDefaultAsync(tx);
        return row is null ? null : Row(row);
    }

    /// <summary>Opening + cash taken − change given + additions − expenses − withdrawals − transfers.</summary>
    private static async Task<JsonObject> CashBreakdown(QueryFactory q, IDictionary<string, object?> s, DbTransaction? tx, CancellationToken ct = default)
    {
        var id = (string)s["session_id"]!;
        var cashSales = await q.Query("sales.sale_payment as p").Join("sales.sale as s", "s.sale_id", "p.sale_id")
            .Where("s.session_id", id).Where("s.status", "<>", "voided").Where("p.method", "cash")
            .SumAsync<long?>("p.amount_h", tx) ?? 0;
        var movements = (await q.Query("sales.session_movement").Where("session_id", id)
            .Select("movement_type").SelectRaw("SUM(amount_h) AS total").GroupBy("movement_type").GetAsync(tx))
            .Select(Row).ToDictionary(r => (string)r["movement_type"]!, r => Convert.ToInt64(r["total"]));
        long M(string t) => movements.GetValueOrDefault(t);
        var opening = Convert.ToInt64(s["opening_balance_h"]);
        var expected = opening + cashSales + M("addition") - M("expense") - M("withdrawal") - M("transfer");
        return new JsonObject
        {
            ["openingH"] = opening, ["salesH"] = cashSales, ["additionsH"] = M("addition"), ["refundsH"] = 0,
            ["expensesH"] = M("expense"), ["withdrawalsH"] = M("withdrawal"), ["transfersH"] = M("transfer"), ["expectedH"] = expected,
        };
    }

    private static Task Audit(QueryFactory q, DbTransaction tx, string sessionId, string action, string description,
        string? oldValue, string? newValue, string user, DateTime at) =>
        q.Query("sales.session_audit").InsertAsync(new Dictionary<string, object?>
        {
            ["session_audit_id"] = $"rsa_{Guid.NewGuid():N}"[..20], ["session_id"] = sessionId, ["action"] = action,
            ["description"] = description, ["old_value"] = oldValue, ["new_value"] = newValue, ["user_name"] = user, ["created_at_utc"] = at,
        }, tx);

    /// <summary>Before 05:00 Riyadh time the night still belongs to the previous business day.</summary>
    internal static DateTime RiyadhBusinessDate(DateTime utc)
    {
        var riyadh = utc.AddHours(3);
        return (riyadh.Hour < 5 ? riyadh.AddDays(-1) : riyadh).Date;
    }

    internal static IDictionary<string, object?> Row(object row) => (IDictionary<string, object?>)row;

    private static string Iso(object? v) => v is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : "";

    internal static JsonObject SessionDoc(IDictionary<string, object?> r)
    {
        var details = CatalogEndpoints.Parse(r["details_json"]);
        var doc = new JsonObject
        {
            ["id"] = (string)r["session_id"]!, ["number"] = Convert.ToInt64(r["session_no"]).ToString(),
            ["branchId"] = (string)r["branch_id"]!, ["registerId"] = (string)r["register_id"]!,
            ["businessDate"] = r["business_date"] is DateTime bd ? bd.ToString("yyyy-MM-dd") : r["business_date"]?.ToString(),
            ["status"] = (string)r["status"]!, ["openedAt"] = Iso(r["opened_at_utc"]), ["openedBy"] = (string)r["opened_by"]!,
            ["openedById"] = r["opened_by_id"] as string, ["openingBalanceH"] = Convert.ToInt64(r["opening_balance_h"]),
            ["openingNotes"] = r["opening_notes"] as string ?? "", ["lastOrderNo"] = Convert.ToInt32(r["last_order_no"]),
            ["reopenCount"] = Convert.ToInt32(r["reopen_count"]),
            ["createdAt"] = Iso(r["created_at_utc"]), ["updatedAt"] = Iso(r["updated_at_utc"]),
        };
        if (r["closed_at_utc"] is DateTime) doc["closedAt"] = Iso(r["closed_at_utc"]);
        if (r["closed_by"] is string cb) doc["closedBy"] = cb;
        if (r["closed_by_id"] is string cbi) doc["closedById"] = cbi;
        if (r["expected_cash_h"] is not null) doc["expectedClosingH"] = Convert.ToInt64(r["expected_cash_h"]);
        if (r["actual_closing_h"] is not null) doc["actualClosingH"] = Convert.ToInt64(r["actual_closing_h"]);
        if (r["discrepancy_h"] is not null) doc["differenceH"] = Convert.ToInt64(r["discrepancy_h"]);
        if (r["discrepancy_reason"] is string dr) doc["discrepancyReason"] = dr;
        if (r["closing_notes"] is string cn) doc["closingNotes"] = cn;
        if (details?["summary"] is JsonNode summary) doc["summary"] = summary.DeepClone();
        return doc;
    }

    private static JsonObject MovementDoc(IDictionary<string, object?> r) => new()
    {
        ["id"] = (string)r["movement_id"]!, ["sessionId"] = (string)r["session_id"]!, ["type"] = (string)r["movement_type"]!,
        ["amountH"] = Convert.ToInt64(r["amount_h"]), ["description"] = (string)r["description"]!,
        ["createdBy"] = (string)r["user_name"]!, ["createdAt"] = Iso(r["created_at_utc"]),
    };

    private static JsonObject AuditDoc(IDictionary<string, object?> r)
    {
        var doc = new JsonObject
        {
            ["id"] = (string)r["session_audit_id"]!, ["sessionId"] = (string)r["session_id"]!, ["action"] = (string)r["action"]!,
            ["description"] = (string)r["description"]!, ["userName"] = (string)r["user_name"]!, ["at"] = Iso(r["created_at_utc"]),
        };
        if (r["old_value"] is string o) doc["previousValue"] = o;
        if (r["new_value"] is string n) doc["newValue"] = n;
        return doc;
    }

    private static IResult NotFound() =>
        Results.Json(ApiError.Create("not_found", "That business day was not found.", "لم يتم العثور على يوم العمل."), statusCode: 404);

    private static IResult Error(int status, string code, string en, string ar) =>
        Results.Json(ApiError.Create(code, en, ar), statusCode: status);

    private sealed class ConflictException(JsonObject session) : Exception("conflict")
    {
        public JsonObject Session { get; } = session;
    }
}
