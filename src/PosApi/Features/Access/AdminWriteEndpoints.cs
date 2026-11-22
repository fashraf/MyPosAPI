using Dapper;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;

namespace PosApi.Features.Access;

/// <summary>
/// The remaining write paths: discounts, users and the company profile.
///
/// Each was read-only, so each screen saved into memory and lost the change on
/// reload. Validation lives in the procedures rather than here, so the rule is
/// enforced whoever calls it — a check in a handler is a check a background job
/// can walk past.
/// </summary>
public static class AdminWriteEndpoints
{
    public static void MapAdminWriteEndpoints(this IEndpointRouteBuilder app)
    {
        /*-------------------------------------------------------- discounts */

        var discounts = app.MapGroup("/api/discounts")
            .WithTags("Discounts")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        discounts.MapPost("", (SaveDiscountRequest r, ISqlConnectionFactory db,
                               HttpContext h, CancellationToken ct)
                      => SaveDiscountAsync(r with { DiscountId = null }, db, h, ct))
                 .RequireAuthorization(PermissionPolicy.For("discounts.create"));

        discounts.MapPut("/{discountId}", (string discountId, SaveDiscountRequest r,
                                           ISqlConnectionFactory db, HttpContext h,
                                           CancellationToken ct)
                      => SaveDiscountAsync(r with { DiscountId = discountId }, db, h, ct))
                 .RequireAuthorization(PermissionPolicy.For("discounts.edit"));

        /*------------------------------------------------------------ users */

        var users = app.MapGroup("/api/users")
            .WithTags("Users")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        users.MapPost("", (SaveUserRequest r, ISqlConnectionFactory db, ICurrentUser u,
                           HttpContext h, CancellationToken ct)
                  => SaveUserAsync(r with { UserId = null }, db, u, h, ct))
             .RequireAuthorization(PermissionPolicy.For("users.create"));

        users.MapPut("/{userId}", (string userId, SaveUserRequest r,
                                   ISqlConnectionFactory db, ICurrentUser u,
                                   HttpContext h, CancellationToken ct)
                  => SaveUserAsync(r with { UserId = userId }, db, u, h, ct))
             .RequireAuthorization(PermissionPolicy.For("users.edit"));

        /*---------------------------------------------------------- company */

        var company = app.MapGroup("/api/company")
            .WithTags("Company")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        company.MapGet("", GetCompanyAsync);

        company.MapPut("", SaveCompanyAsync)
               .RequireAuthorization(PermissionPolicy.For("company.edit"));
    }

    /*================================================================ handlers */

    private static async Task<IResult> SaveDiscountAsync(
        SaveDiscountRequest request, ISqlConnectionFactory db,
        HttpContext http, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@DiscountId", request.DiscountId);
        parameters.Add("@NameAr", request.NameAr);
        parameters.Add("@NameEn", request.NameEn);
        parameters.Add("@DiscountType", request.DiscountType);
        parameters.Add("@Value", request.Value);
        parameters.Add("@MaxDiscountH", request.MaxDiscountH);
        parameters.Add("@MinSubtotalH", request.MinSubtotalH ?? 0);
        parameters.Add("@ApprovalAboveH", request.ApprovalAboveH);
        parameters.Add("@AppliesTo", request.AppliesTo ?? "sale");
        parameters.Add("@IsAutomatic", request.IsAutomatic ?? false);
        parameters.Add("@AllowStacking", request.AllowStacking ?? false);
        parameters.Add("@ActiveDays", request.ActiveDays);
        parameters.Add("@StartsOn", request.StartsOn);
        parameters.Add("@EndsOn", request.EndsOn);
        parameters.Add("@IsActive", request.IsActive ?? true);
        parameters.Add("@SavedId", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 40);

        await using var connection = db.Create();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "sales.usp_SaveDiscount", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is >= 59020 and <= 59024)
        {
            return Results.UnprocessableEntity(WriteErrors.From(ex));
        }

        var discountId = parameters.Get<string>("@SavedId");
        http.Items["AuditEntityId"] = discountId;

        return Results.Ok(new { discountId });
    }

    private static async Task<IResult> SaveUserAsync(
        SaveUserRequest request, ISqlConnectionFactory db, ICurrentUser current,
        HttpContext http, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@UserId", request.UserId);
        parameters.Add("@Username", request.Username);
        parameters.Add("@NameAr", request.NameAr);
        parameters.Add("@NameEn", request.NameEn);
        parameters.Add("@Email", Blank(request.Email));
        parameters.Add("@Phone", Blank(request.Phone));
        parameters.Add("@RoleId", request.RoleId);
        parameters.Add("@Status", request.Status ?? "active");
        parameters.Add("@IsExternal", request.IsExternal ?? false);
        parameters.Add("@AccessExpiresOn", request.AccessExpiresOn);
        parameters.Add("@BaseSalaryH", request.BaseSalaryH ?? 0);
        parameters.Add("@Actor", current.Username);
        parameters.Add("@SavedId", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 40);

        await using var connection = db.Create();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "core.usp_SaveUser", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is >= 59030 and <= 59037)
        {
            return Results.UnprocessableEntity(WriteErrors.From(ex));
        }

        var userId = parameters.Get<string>("@SavedId");
        http.Items["AuditEntityId"] = userId;

        /* No password is returned or set here. It arrives through the sign-in
           flow, so it never passes through an admin form or an audit row. */
        return Results.Ok(new { userId });
    }

    private static async Task<IResult> GetCompanyAsync(
        ISqlConnectionFactory db, CancellationToken ct = default)
    {
        await using var connection = db.Create();

        var row = await connection.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT TOP (1) * FROM core.Company;", cancellationToken: ct));

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    private static async Task<IResult> SaveCompanyAsync(
        SaveCompanyRequest request, ISqlConnectionFactory db,
        CancellationToken ct = default)
    {
        await using var connection = db.Create();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "core.usp_SaveCompany",
                new
                {
                    request.NameAr, request.NameEn,
                    request.DescriptionAr, request.DescriptionEn, request.LogoUrl,
                    CrNumber = Blank(request.CrNumber), VatNumber = Blank(request.VatNumber),
                    Phone = Blank(request.Phone), Email = Blank(request.Email),
                    Website = Blank(request.Website),
                    request.Street, request.District, request.City,
                    request.PostalCode, request.Country
                },
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 59040 or 59041)
        {
            return Results.UnprocessableEntity(WriteErrors.From(ex));
        }

        return Results.Ok(new { saved = true });
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/*========================================================== request shapes */

public sealed record SaveDiscountRequest
{
    public string? DiscountId { get; init; }
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string DiscountType { get; init; } = "percent";
    public decimal Value { get; init; }
    public long? MaxDiscountH { get; init; }
    public long? MinSubtotalH { get; init; }
    public long? ApprovalAboveH { get; init; }
    public string? AppliesTo { get; init; }
    public bool? IsAutomatic { get; init; }
    public bool? AllowStacking { get; init; }
    public string? ActiveDays { get; init; }
    public DateOnly? StartsOn { get; init; }
    public DateOnly? EndsOn { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record SaveUserRequest
{
    public string? UserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string RoleId { get; init; } = string.Empty;
    public string? Status { get; init; }
    public bool? IsExternal { get; init; }
    public DateOnly? AccessExpiresOn { get; init; }
    public long? BaseSalaryH { get; init; }
}

public sealed record SaveCompanyRequest(
    string NameAr, string NameEn, string? DescriptionAr, string? DescriptionEn,
    string? LogoUrl, string? CrNumber, string? VatNumber, string? Phone,
    string? Email, string? Website, string? Street, string? District,
    string? City, string? PostalCode, string? Country);
