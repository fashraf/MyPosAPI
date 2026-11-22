using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;

namespace PosApi.Features.Sales;

public sealed record OpenSessionRequest(
    string BranchId, string? RegisterId, long OpeningBalanceH, DateTime? OpenedAt, DateOnly? BusinessDate,
    string? Notes, string? IdempotencyKey);
public sealed record CloseSessionRequest(
    long ActualClosingH, string? ClosingNotes, string? DiscrepancyReason, string? PendingOverrideReason,
    JsonElement? Summary, string? IdempotencyKey);
public sealed record CashMovementRequest(string Type, long AmountH, string Description);
public sealed record ReopenRequest(string Reason);
public sealed record SessionSettingsRequest(int LongSessionHours, long DiscrepancyThresholdH);

/// <summary>
/// Restaurant business sessions: open, move cash, close, reopen, history.
///
/// Every rule lives in the database (db-migrations/28-restaurant-sessions.sql)
/// so it holds no matter which client calls: one open session per register
/// (filtered unique index), no order without an open session (trigger, error
/// 52090), a reason for every cash difference (check constraint), and an audit
/// log that cannot be edited (INSTEAD OF trigger). This file only maps HTTP to
/// those procedures and turns their errors into bilingual responses.
/// </summary>
public static class RestaurantSessionEndpoints
{
    private const string View = "restaurant.session.view";

    public static void MapRestaurantSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/restaurant-sessions")
            .WithTags("Restaurant sessions")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        g.MapGet("/status", StatusAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapGet("/current", CurrentAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapGet("/settings", SettingsAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapPut("/settings", SaveSettingsAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.approve_discrepancy"));
        g.MapGet("/history", HistoryAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.view_history"));
        g.MapGet("/{id}", GetAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapGet("/{id}/sales-summary", SalesSummaryAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapGet("/{id}/payment-summary", PaymentSummaryAsync).RequireAuthorization(PermissionPolicy.For(View));
        g.MapGet("/{id}/cash-movements", MovementsAsync).RequireAuthorization(PermissionPolicy.For("restaurant.cash_movement.view"));
        g.MapPost("/{id}/cash-movements", AddMovementAsync).RequireAuthorization(PermissionPolicy.For("restaurant.cash_movement.create"));
        g.MapGet("/{id}/closing-preview", ClosingPreviewAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.reconcile"));
        g.MapGet("/{id}/report", ReportAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.view_reports"));
        g.MapGet("/{id}/audit-log", AuditAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.view_history"));
        g.MapPost("/open", OpenAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.open"));
        g.MapPost("/{id}/close", CloseAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.close"));
        g.MapPost("/{id}/reopen", ReopenAsync).RequireAuthorization(PermissionPolicy.For("restaurant.session.reopen"));

        var tax = app.MapGroup("/api/settings/tax").WithTags("Tax").RequireAuthorization();
        tax.MapGet("", TaxAsync);
        tax.MapPut("", SaveTaxAsync).RequireAuthorization(PermissionPolicy.For("settings.edit"));
    }

    /* --------------------------------------------------------------- reads */

    private static async Task<IResult> StatusAsync(string? branchId, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var s = await c.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT TOP (1) * FROM rest.RestaurantSession WHERE Status = N'open' AND (@b IS NULL OR BranchId = @b) ORDER BY OpenedAtUtc DESC;",
            new { b = branchId }, cancellationToken: ct));
        return Results.Ok(new { open = s is not null, session = s });
    }

    private static async Task<IResult> CurrentAsync(string? branchId, bool? includeClosed, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var s = await c.QuerySingleOrDefaultAsync(new CommandDefinition(
            """
            SELECT TOP (1) * FROM rest.RestaurantSession
            WHERE (@b IS NULL OR BranchId = @b) AND (@all = 1 OR Status = N'open')
            ORDER BY OpenedAtUtc DESC;
            """,
            new { b = branchId, all = includeClosed == true }, cancellationToken: ct));
        /* No open session is a normal state, not a 404. */
        return Results.Ok(s);
    }

    private static async Task<IResult> GetAsync(string id, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var s = await c.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM rest.RestaurantSession WHERE SessionId = @id;", new { id }, cancellationToken: ct));
        return s is null ? NotFound() : Results.Ok(s);
    }

    private static async Task<IResult> SalesSummaryAsync(string id, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var frozen = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT SummaryJson FROM rest.RestaurantSession WHERE SessionId = @id AND Status = N'closed';", new { id }, cancellationToken: ct));
        if (frozen is not null) return Results.Text(frozen, "application/json");

        var row = await c.QuerySingleOrDefaultAsync(new CommandDefinition(
            """
            SELECT
              COUNT(*)                                                                    AS orders,
              SUM(CASE WHEN s.Status <> N'voided' OR ISNULL(s.VoidSessionId, s.SessionId) <> s.SessionId THEN 1 ELSE 0 END) AS completed,
              SUM(CASE WHEN s.Status = N'voided' AND ISNULL(s.VoidSessionId, s.SessionId) = s.SessionId THEN 1 ELSE 0 END)  AS cancelled,
              SUM(CASE WHEN s.Status = N'voided' AND ISNULL(s.VoidSessionId, s.SessionId) = s.SessionId THEN 0 ELSE s.TotalH END)    AS totalH,
              SUM(CASE WHEN s.Status = N'voided' AND ISNULL(s.VoidSessionId, s.SessionId) = s.SessionId THEN 0 ELSE s.TaxH END)      AS vatH,
              SUM(CASE WHEN s.Status = N'voided' AND ISNULL(s.VoidSessionId, s.SessionId) = s.SessionId THEN 0 ELSE s.DiscountH END) AS discountsH
            FROM sales.Sale AS s WHERE s.SessionId = @id;
            """, new { id }, cancellationToken: ct));
        var cash = await c.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM rest.fn_ExpectedCash(@id);", new { id }, cancellationToken: ct));
        return Results.Ok(new { totals = row, cash });
    }

    private static async Task<IResult> PaymentSummaryAsync(string id, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var rows = await c.QueryAsync(new CommandDefinition(
            "SELECT Method AS method, Orders AS [count], AmountH AS amountH FROM rest.vw_SessionMethodTotals WHERE SessionId = @id;",
            new { id }, cancellationToken: ct));
        return Results.Ok(rows);
    }

    private static async Task<IResult> MovementsAsync(string id, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var rows = await c.QueryAsync(new CommandDefinition(
            "SELECT * FROM rest.CashMovement WHERE SessionId = @id ORDER BY CreatedAtUtc DESC;", new { id }, cancellationToken: ct));
        return Results.Ok(rows);
    }

    private static async Task<IResult> AuditAsync(string id, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var rows = await c.QueryAsync(new CommandDefinition(
            "SELECT * FROM rest.SessionAuditLog WHERE SessionId = @id ORDER BY AtUtc;", new { id }, cancellationToken: ct));
        return Results.Ok(rows);
    }

    private static async Task<IResult> ClosingPreviewAsync(string id, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var session = await c.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM rest.RestaurantSession WHERE SessionId = @id;", new { id }, cancellationToken: ct));
        if (session is null) return NotFound();
        var cash = await c.QuerySingleAsync(new CommandDefinition("SELECT * FROM rest.fn_ExpectedCash(@id);", new { id }, cancellationToken: ct));
        var methods = await c.QueryAsync(new CommandDefinition(
            "SELECT Method AS method, Orders AS [count], AmountH AS amountH FROM rest.vw_SessionMethodTotals WHERE SessionId = @id;",
            new { id }, cancellationToken: ct));
        var settings = await c.QuerySingleAsync(new CommandDefinition("SELECT * FROM rest.SessionSettings;", cancellationToken: ct));
        return Results.Ok(new { session, cash, methods, settings });
    }

    private static Task<IResult> ReportAsync(string id, ISqlConnectionFactory db, CancellationToken ct) => GetAsync(id, db, ct);

    private static async Task<IResult> HistoryAsync(
        string? branchId, DateOnly? from, DateOnly? to, string? status, string? difference, string? user, string? search,
        ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var rows = await c.QueryAsync(new CommandDefinition(
            """
            SELECT TOP (500) SessionId, SessionNumber, BranchId, RegisterId, BusinessDate, Status, OpenedAtUtc, OpenedByName,
                   OpeningBalanceH, ClosedAtUtc, ClosedByName, ExpectedClosingH, ActualClosingH, DifferenceH,
                   JSON_VALUE(SummaryJson, '$.totalH') AS TotalSalesH
            FROM rest.RestaurantSession
            WHERE (@b IS NULL OR BranchId = @b)
              AND (@from IS NULL OR BusinessDate >= @from) AND (@to IS NULL OR BusinessDate <= @to)
              AND (@status IS NULL OR @status = N'all' OR Status = @status)
              AND (@diff IS NULL OR @diff = N'all'
                   OR (@diff = N'shortage' AND DifferenceH < 0) OR (@diff = N'surplus' AND DifferenceH > 0)
                   OR (@diff = N'balanced' AND DifferenceH = 0))
              AND (@user IS NULL OR OpenedByName LIKE N'%' + @user + N'%' OR ClosedByName LIKE N'%' + @user + N'%')
              AND (@q IS NULL OR SessionNumber LIKE N'%' + @q + N'%' OR CONVERT(NVARCHAR(10), BusinessDate, 23) LIKE N'%' + @q + N'%')
            ORDER BY OpenedAtUtc DESC;
            """,
            new { b = branchId, from, to, status, diff = difference, user, q = search }, cancellationToken: ct));
        return Results.Ok(rows);
    }

    private static async Task<IResult> SettingsAsync(ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        return Results.Ok(await c.QuerySingleAsync(new CommandDefinition(
            "SELECT LongSessionHours AS longSessionHours, DiscrepancyThresholdH AS discrepancyThresholdH FROM rest.SessionSettings;", cancellationToken: ct)));
    }

    /* -------------------------------------------------------------- writes */

    private static async Task<IResult> OpenAsync(OpenSessionRequest r, ISqlConnectionFactory db, ICurrentUser me, CancellationToken ct)
    {
        if (!me.CanSeeBranch(r.BranchId)) return Results.Forbid();
        await using var c = db.Create();
        var p = new DynamicParameters();
        p.Add("@BranchId", r.BranchId);
        p.Add("@RegisterId", r.RegisterId ?? "reg_main");
        p.Add("@OpeningBalanceH", r.OpeningBalanceH);
        p.Add("@OpenedAtUtc", r.OpenedAt?.ToUniversalTime());
        p.Add("@BusinessDate", r.BusinessDate);
        p.Add("@Notes", r.Notes);
        p.Add("@UserId", me.UserId);
        p.Add("@UserName", me.Username);
        p.Add("@SessionId", dbType: DbType.String, direction: ParameterDirection.Output, size: 40);
        try
        {
            await c.ExecuteAsync(new CommandDefinition("rest.usp_OpenSession", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (IsRule(ex)) { return Fail(ex); }
        return await GetAsync(p.Get<string>("@SessionId"), db, ct);
    }

    private static async Task<IResult> AddMovementAsync(string id, CashMovementRequest r, ISqlConnectionFactory db, ICurrentUser me, CancellationToken ct)
    {
        var type = r.Type.ToLowerInvariant() switch
        {
            "addition" => "CASH_ADDITION", "expense" => "CASH_EXPENSE", "withdrawal" => "CASH_WITHDRAWAL",
            "transfer" => "CASH_TRANSFER", var other => other.ToUpperInvariant(),
        };
        await using var c = db.Create();
        var p = new DynamicParameters(new { SessionId = id, Type = type, r.AmountH, r.Description, UserId = me.UserId, UserName = me.Username });
        p.Add("@MovementId", dbType: DbType.String, direction: ParameterDirection.Output, size: 40);
        try
        {
            await c.ExecuteAsync(new CommandDefinition("rest.usp_AddCashMovement", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (IsRule(ex)) { return Fail(ex); }
        return Results.Created($"/api/restaurant-sessions/{id}/cash-movements", new { movementId = p.Get<string>("@MovementId") });
    }

    private static async Task<IResult> CloseAsync(string id, CloseSessionRequest r, ISqlConnectionFactory db, ICurrentUser me, CancellationToken ct)
    {
        await using var c = db.Create();
        var threshold = await c.ExecuteScalarAsync<long>(new CommandDefinition("SELECT DiscrepancyThresholdH FROM rest.SessionSettings;", cancellationToken: ct));
        try
        {
            var closed = await c.QuerySingleAsync(new CommandDefinition("rest.usp_CloseSession", new
            {
                SessionId = id,
                r.ActualClosingH,
                r.ClosingNotes,
                r.DiscrepancyReason,
                r.PendingOverrideReason,
                CanApprove = me.Can("restaurant.session.approve_discrepancy"),
                ThresholdH = threshold,
                SummaryJson = r.Summary?.GetRawText(),
                UserId = me.UserId,
                UserName = me.Username,
            }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
            return Results.Ok(closed);
        }
        catch (SqlException ex) when (IsRule(ex)) { return Fail(ex); }
    }

    private static async Task<IResult> ReopenAsync(string id, ReopenRequest r, ISqlConnectionFactory db, ICurrentUser me, CancellationToken ct)
    {
        await using var c = db.Create();
        try
        {
            await c.ExecuteAsync(new CommandDefinition("rest.usp_ReopenSession",
                new { SessionId = id, r.Reason, UserId = me.UserId, UserName = me.Username },
                commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (IsRule(ex) || ex.Number == 2601) { return Fail(ex); }
        return await GetAsync(id, db, ct);
    }

    private static async Task<IResult> SaveSettingsAsync(SessionSettingsRequest r, ISqlConnectionFactory db, ICurrentUser me, CancellationToken ct)
    {
        if (r.LongSessionHours <= 0 || r.DiscrepancyThresholdH < 0)
            return Results.Json(ApiError.Create("invalid_settings", "Thresholds must be positive numbers.", "يجب أن تكون الحدود أرقامًا موجبة."), statusCode: 422);
        await using var c = db.Create();
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE rest.SessionSettings SET LongSessionHours = @h, DiscrepancyThresholdH = @t, UpdatedAtUtc = SYSUTCDATETIME(), UpdatedBy = @by;
            INSERT rest.SessionAuditLog (Action, Description, NewValue, UserId, UserName)
            VALUES (N'settings_changed', N'Alert thresholds changed', CONCAT(@h, N'h / ', @t), @uid, @by);
            """, new { h = r.LongSessionHours, t = r.DiscrepancyThresholdH, by = me.Username, uid = me.UserId }, cancellationToken: ct));
        return await SettingsAsync(db, ct);
    }

    private static async Task<IResult> TaxAsync(ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var json = await c.ExecuteScalarAsync<string>(new CommandDefinition("SELECT SettingsJson FROM core.TaxSettings WHERE SettingsId = 1;", cancellationToken: ct));
        return Results.Text(json ?? "{}", "application/json");
    }

    private static async Task<IResult> SaveTaxAsync(JsonElement body, ISqlConnectionFactory db, ICurrentUser me, CancellationToken ct)
    {
        bool enabled = body.TryGetProperty("enabled", out var e) && e.GetBoolean();
        string country = body.TryGetProperty("country", out var co) ? co.GetString() ?? "SA" : "SA";
        string regime = body.TryGetProperty("regime", out var re) ? re.GetString() ?? "vat" : "vat";
        bool inclusive = body.TryGetProperty("pricesIncludeTax", out var pi) && pi.GetBoolean();
        string? reg = body.TryGetProperty("registrationNumber", out var rn) ? rn.GetString() : null;
        if (!body.TryGetProperty("rates", out var rates) || rates.GetArrayLength() == 0)
            return Results.Json(ApiError.Create("validation_failed", "Add at least one rate.", "أضف نسبة واحدة على الأقل."), statusCode: 422);

        await using var c = db.Create();
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE core.TaxSettings
               SET Enabled = @enabled, Country = @country, Regime = @regime, PricesIncludeTax = @inclusive,
                   RegistrationNumber = @reg, SettingsJson = @json, UpdatedAtUtc = SYSUTCDATETIME(), UpdatedBy = @by
             WHERE SettingsId = 1;
            """, new { enabled, country, regime, inclusive, reg, json = body.GetRawText(), by = me.Username }, cancellationToken: ct));
        return await TaxAsync(db, ct);
    }

    /* -------------------------------------------------------------- errors */

    private static bool IsRule(SqlException ex) => (ex.Number >= 56000 && ex.Number < 56100) || ex.Number == 52090;

    private static IResult NotFound() =>
        Results.Json(ApiError.Create("SESSION_NOT_FOUND", "That session does not exist.", "هذه الجلسة غير موجودة."), statusCode: 404);

    private static IResult Fail(SqlException ex)
    {
        var (status, code, en, ar) = ex.Number switch
        {
            52090 => (409, "RESTAURANT_CLOSED", "Restaurant is currently closed. Open a business session before creating orders.", "المطعم مغلق حاليًا. افتح جلسة عمل قبل إنشاء الطلبات."),
            56000 => (422, "INVALID_OPENING_BALANCE", "Opening cash must be zero or more.", "يجب أن يكون النقد الافتتاحي صفرًا أو أكثر."),
            56001 or 2601 => (409, "SESSION_ALREADY_OPEN", "This restaurant already has an open business session.", "يوجد لهذا المطعم جلسة عمل مفتوحة بالفعل."),
            56002 => (422, "OPENED_IN_FUTURE", "Opening time cannot be in the future.", "لا يمكن أن يكون وقت الفتح في المستقبل."),
            56003 => (422, "OVERLAPS_PREVIOUS", "Opening time must be after the previous session closed.", "يجب أن يكون وقت الفتح بعد إغلاق الجلسة السابقة."),
            56010 => (422, "INVALID_AMOUNT", "Enter an amount greater than zero.", "أدخل مبلغًا أكبر من صفر."),
            56011 => (422, "DESCRIPTION_REQUIRED", "Say what the cash was for.", "اذكر الغرض من هذا النقد."),
            56012 => (409, "SESSION_CLOSED", "This session is closed.", "هذه الجلسة مغلقة."),
            56013 => (422, "INSUFFICIENT_CASH", "There is not that much cash expected in the drawer.", "لا يوجد هذا المبلغ في الدرج."),
            56020 => (422, "INVALID_ACTUAL_CASH", "Enter the cash you counted (zero or more).", "أدخل النقد المعدود (صفر أو أكثر)."),
            56021 => (404, "SESSION_NOT_FOUND", "That session does not exist.", "هذه الجلسة غير موجودة."),
            56022 => (409, "SESSION_ALREADY_CLOSED", "This session was already closed.", "أُغلقت هذه الجلسة بالفعل."),
            56023 => (409, "PENDING_ORDERS", "Orders are still in the kitchen.", "ما زالت هناك طلبات في المطبخ."),
            56024 => (422, "OVERRIDE_REASON_REQUIRED", "Give a reason for closing with orders still open.", "اذكر سبب الإغلاق مع وجود طلبات مفتوحة."),
            56025 => (422, "DISCREPANCY_REASON_REQUIRED", "Explain the cash difference before closing.", "اشرح فرق النقد قبل الإغلاق."),
            56026 => (403, "APPROVAL_REQUIRED", "A manager must approve this difference.", "يجب أن يعتمد المدير هذا الفرق."),
            56030 => (422, "REASON_REQUIRED", "Say why the session is being reopened.", "اذكر سبب إعادة فتح الجلسة."),
            56031 => (409, "SESSION_NOT_CLOSED", "Only a closed session can be reopened.", "يمكن إعادة فتح الجلسة المغلقة فقط."),
            56032 => (409, "NOT_LATEST", "Only the most recent session can be reopened.", "يمكن إعادة فتح أحدث جلسة فقط."),
            _ => (422, "SESSION_RULE", ex.Message, ex.Message),
        };
        /* The spec's order-guard shape, alongside the bilingual payload. */
        return Results.Json(new { success = false, error = code, message = en, messageAr = ar }, statusCode: status);
    }
}
