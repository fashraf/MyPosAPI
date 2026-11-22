using Dapper;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;

namespace PosApi.Features.Access;

/// <summary>
/// The last tables without a way in: branch access, permission overrides,
/// commission rules and bill templates.
///
/// Access and overrides are set wholesale rather than granted and revoked one
/// at a time. A screen shows a set, and applying a diff means two round trips
/// that can half succeed — leaving someone with access nobody chose.
/// </summary>
public static class AccessEndpoints
{
    public static void MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users")
            .WithTags("Users")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        users.MapGet("/{userId}/access", GetAccessAsync)
             .RequireAuthorization(PermissionPolicy.For("users.view"));

        users.MapPut("/{userId}/branches", SetBranchesAsync)
             .RequireAuthorization(PermissionPolicy.For("users.assign_branches"));

        users.MapPut("/{userId}/overrides", SetOverridesAsync)
             .RequireAuthorization(PermissionPolicy.For("users.override"));

        var commission = app.MapGroup("/api/finance/commission-rules")
            .WithTags("Finance")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        commission.MapGet("", ListRulesAsync)
                  .RequireAuthorization(PermissionPolicy.For("finance.view"));

        commission.MapPost("", (SaveRuleRequest r, ISqlConnectionFactory db, CancellationToken ct)
                      => SaveRuleAsync(r with { RuleId = null }, db, ct))
                  .RequireAuthorization(PermissionPolicy.For("finance.commission_rules"));

        commission.MapPut("/{ruleId}", (string ruleId, SaveRuleRequest r,
                                        ISqlConnectionFactory db, CancellationToken ct)
                      => SaveRuleAsync(r with { RuleId = ruleId }, db, ct))
                  .RequireAuthorization(PermissionPolicy.For("finance.commission_rules"));

        var bills = app.MapGroup("/api/bill-templates")
            .WithTags("Bill templates")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        bills.MapGet("", ListTemplatesAsync);

        bills.MapPost("", (SaveTemplateRequest r, ISqlConnectionFactory db, CancellationToken ct)
                  => SaveTemplateAsync(r with { BillId = null }, db, ct))
             .RequireAuthorization(PermissionPolicy.For("bill_builder.edit"));

        bills.MapPut("/{billId}", (string billId, SaveTemplateRequest r,
                                   ISqlConnectionFactory db, CancellationToken ct)
                  => SaveTemplateAsync(r with { BillId = billId }, db, ct))
             .RequireAuthorization(PermissionPolicy.For("bill_builder.edit"));
    }

    /*================================================================ access */

    /// <summary>
    /// A user's branches, their overrides, and what those add up to.
    ///
    /// The effective set is returned alongside the overrides because the two
    /// answer different questions: what was chosen, and what it produced. A
    /// screen showing only the overrides cannot tell an admin that a denial has
    /// taken away something the role still appears to grant.
    /// </summary>
    private static async Task<IResult> GetAccessAsync(
        string userId, ISqlConnectionFactory db, CancellationToken ct = default)
    {
        await using var connection = db.Create();

        await using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT b.BranchId, b.Code, b.NameAr, b.NameEn
            FROM core.UserBranch AS ub
            JOIN core.Branch AS b ON b.BranchId = ub.BranchId
            WHERE ub.UserId = @UserId
            ORDER BY b.Code;

            SELECT PermissionKey, Effect
            FROM core.UserPermissionOverride
            WHERE UserId = @UserId
            ORDER BY PermissionKey;

            SELECT PermissionKey
            FROM core.vw_EffectivePermission
            WHERE UserId = @UserId
            ORDER BY PermissionKey;
            """,
            new { UserId = userId }, cancellationToken: ct));

        return Results.Ok(new
        {
            branches = (await multi.ReadAsync()).ToList(),
            overrides = (await multi.ReadAsync()).ToList(),
            effective = (await multi.ReadAsync<string>()).ToList()
        });
    }

    private static async Task<IResult> SetBranchesAsync(
        string userId, SetBranchesRequest request, ISqlConnectionFactory db,
        HttpContext http, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@UserId", userId);
        parameters.Add("@BranchIds", string.Join(',', request.BranchIds ?? []));
        parameters.Add("@Blocked", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 400);

        await using var connection = db.Create();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "core.usp_SetUserBranches", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 60000)
        {
            return Results.NotFound(ApiError.Create(
                "not_found", ex.Message, "لا يوجد مستخدم بهذا المعرّف."));
        }

        var blocked = parameters.Get<string?>("@Blocked");

        if (!string.IsNullOrWhiteSpace(blocked))
        {
            return Results.Conflict(ApiError.Create("access_blocked", blocked, blocked));
        }

        http.Items["AuditEntityId"] = userId;
        return Results.Ok(new { userId, branches = request.BranchIds?.Length ?? 0 });
    }

    private static async Task<IResult> SetOverridesAsync(
        string userId, SetOverridesRequest request, ISqlConnectionFactory db,
        HttpContext http, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@UserId", userId);
        parameters.Add("@Grants", string.Join(',', request.Grants ?? []));
        parameters.Add("@Denies", string.Join(',', request.Denies ?? []));
        parameters.Add("@Blocked", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 400);

        await using var connection = db.Create();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "core.usp_SetUserOverrides", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 60010)
        {
            return Results.NotFound(ApiError.Create(
                "not_found", ex.Message, "لا يوجد مستخدم بهذا المعرّف."));
        }

        var blocked = parameters.Get<string?>("@Blocked");

        if (!string.IsNullOrWhiteSpace(blocked))
        {
            return Results.Conflict(ApiError.Create("override_blocked", blocked, blocked));
        }

        http.Items["AuditEntityId"] = userId;
        return Results.Ok(new { userId });
    }

    /*============================================================ commission */

    private static async Task<IResult> ListRulesAsync(
        ISqlConnectionFactory db, CancellationToken ct = default)
    {
        await using var connection = db.Create();

        var rows = await connection.QueryAsync(new CommandDefinition(
            "SELECT * FROM fin.CommissionRule ORDER BY Basis;", cancellationToken: ct));

        return Results.Ok(rows);
    }

    private static async Task<IResult> SaveRuleAsync(
        SaveRuleRequest request, ISqlConnectionFactory db, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@RuleId", request.RuleId);
        parameters.Add("@Basis", request.Basis);
        parameters.Add("@Value", request.Value);
        parameters.Add("@IsActive", request.IsActive ?? true);
        parameters.Add("@SavedId", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 40);

        await using var connection = db.Create();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "fin.usp_SaveCommissionRule", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is >= 60020 and <= 60023)
        {
            return Results.UnprocessableEntity(WriteErrors.From(ex));
        }

        return Results.Ok(new { ruleId = parameters.Get<string>("@SavedId") });
    }

    /*============================================================= templates */

    private static async Task<IResult> ListTemplatesAsync(
        ISqlConnectionFactory db, CancellationToken ct = default)
    {
        await using var connection = db.Create();

        var rows = await connection.QueryAsync(new CommandDefinition(
            "SELECT * FROM core.BillTemplate ORDER BY Audience, Name;",
            cancellationToken: ct));

        return Results.Ok(rows);
    }

    private static async Task<IResult> SaveTemplateAsync(
        SaveTemplateRequest request, ISqlConnectionFactory db, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@BillId", request.BillId);
        parameters.Add("@Name", request.Name);
        parameters.Add("@Audience", request.Audience);
        parameters.Add("@PrintGroupId", request.PrintGroupId);
        parameters.Add("@IsDefault", request.IsDefault ?? false);
        parameters.Add("@PaperSize", request.PaperSize ?? "80mm");
        parameters.Add("@BillLanguage", request.BillLanguage ?? "both");
        parameters.Add("@ShowLogo", request.ShowLogo ?? true);
        parameters.Add("@ShowVatNumber", request.ShowVatNumber ?? true);
        parameters.Add("@ShowCrNumber", request.ShowCrNumber ?? false);
        parameters.Add("@ShowUnitPrice", request.ShowUnitPrice ?? true);
        parameters.Add("@ShowLineTotal", request.ShowLineTotal ?? true);
        parameters.Add("@ShowSubtotal", request.ShowSubtotal ?? true);
        parameters.Add("@ShowDiscount", request.ShowDiscount ?? true);
        parameters.Add("@ShowVat", request.ShowVat ?? true);
        parameters.Add("@ShowGrandTotal", request.ShowGrandTotal ?? true);
        parameters.Add("@ShowZatcaQr", request.ShowZatcaQr ?? true);
        parameters.Add("@FooterAr", request.FooterAr);
        parameters.Add("@FooterEn", request.FooterEn);
        parameters.Add("@IsActive", request.IsActive ?? true);
        parameters.Add("@SavedId", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 40);

        await using var connection = db.Create();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "core.usp_SaveBillTemplate", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is >= 60030 and <= 60033)
        {
            return Results.UnprocessableEntity(WriteErrors.From(ex));
        }

        return Results.Ok(new { billId = parameters.Get<string>("@SavedId") });
    }
}

public sealed record SetBranchesRequest(string[]? BranchIds);
public sealed record SetOverridesRequest(string[]? Grants, string[]? Denies);
public sealed record SaveRuleRequest
{
    public string? RuleId { get; init; }
    public string Basis { get; init; } = "percent_of_sale";
    public decimal Value { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record SaveTemplateRequest
{
    public string? BillId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Audience { get; init; } = "customer";
    public string? PrintGroupId { get; init; }
    public bool? IsDefault { get; init; }
    public string? PaperSize { get; init; }
    public string? BillLanguage { get; init; }
    public bool? ShowLogo { get; init; }
    public bool? ShowVatNumber { get; init; }
    public bool? ShowCrNumber { get; init; }
    public bool? ShowUnitPrice { get; init; }
    public bool? ShowLineTotal { get; init; }
    public bool? ShowSubtotal { get; init; }
    public bool? ShowDiscount { get; init; }
    public bool? ShowVat { get; init; }
    public bool? ShowGrandTotal { get; init; }
    public bool? ShowZatcaQr { get; init; }
    public string? FooterAr { get; init; }
    public string? FooterEn { get; init; }
    public bool? IsActive { get; init; }
}
