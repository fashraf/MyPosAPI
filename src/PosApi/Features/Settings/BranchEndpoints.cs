using Dapper;
using PosApi.Audit;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;

namespace PosApi.Features.Settings;

/// <summary>
/// Creating and editing branches.
///
/// A branch is three rows, not one: the branch, its settings, and someone with
/// access to it. The procedure writes all three in one transaction. Creating
/// only the branch would leave a till guessing its VAT rate, and an owner who
/// cannot select the branch they just made.
/// </summary>
public static class BranchEndpoints
{
    public static void MapBranchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches")
            .WithTags("Branches")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        /* The full record for the maintenance screen, settings included. The
           existing list returns only what a branch picker needs. */
        group.MapGet("/manage", ListForManagementAsync)
             .RequireAuthorization(PermissionPolicy.For("branches.edit"));

        group.MapPost("", (SaveBranchRequest r, ISqlConnectionFactory db, ICurrentUser u,
                           HttpContext h, CancellationToken ct)
                  => SaveAsync(r with { BranchId = null }, db, u, h, ct))
             .RequireAuthorization(PermissionPolicy.For("branches.create"));

        group.MapPut("/{branchId}", (string branchId, SaveBranchRequest r,
                                     ISqlConnectionFactory db, ICurrentUser u,
                                     HttpContext h, CancellationToken ct)
                  => SaveAsync(r with { BranchId = branchId }, db, u, h, ct))
             .RequireAuthorization(PermissionPolicy.For("branches.edit"));
    }

    private static async Task<IResult> ListForManagementAsync(
        ISqlConnectionFactory db, CancellationToken ct = default)
    {
        await using var connection = db.Create();

        var rows = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT b.BranchId, b.Code, b.NameAr, b.NameEn, b.Phone,
                   b.AddressAr, b.AddressEn, b.IsActive, b.CreatedAtUtc, b.UpdatedAtUtc,
                   s.VatRatePercent, s.PricesIncludeVat, s.AllowNegativeStock,
                   /* Who can work here. A branch nobody can select is a branch
                      that exists only on paper, and the screen should say so. */
                   (SELECT COUNT(*) FROM core.UserBranch AS ub
                    WHERE ub.BranchId = b.BranchId)          AS StaffCount,
                   (SELECT COUNT(*) FROM device.PosDevice AS d
                    WHERE d.BranchId = b.BranchId AND d.IsActive = 1) AS DeviceCount
            FROM core.Branch AS b
            LEFT JOIN core.BranchSettings AS s ON s.BranchId = b.BranchId
            ORDER BY b.Code;
            """,
            cancellationToken: ct));

        return Results.Ok(rows);
    }

    private static async Task<IResult> SaveAsync(
        SaveBranchRequest request, ISqlConnectionFactory db, ICurrentUser current,
        HttpContext http, CancellationToken ct)
    {
        await using var connection = db.Create();

        /* The row before, so the audit can say what moved. */
        var before = request.BranchId is null ? null
            : await connection.QuerySingleOrDefaultAsync(new CommandDefinition(
                @"SELECT b.Code, b.NameAr, b.NameEn, b.Phone, b.IsActive,
                         s.VatRatePercent, s.PricesIncludeVat, s.AllowNegativeStock
                  FROM core.Branch AS b
                  LEFT JOIN core.BranchSettings AS s ON s.BranchId = b.BranchId
                  WHERE b.BranchId = @BranchId;",
                new { request.BranchId }, cancellationToken: ct));

        var parameters = new DynamicParameters();
        parameters.Add("@BranchId", request.BranchId);
        parameters.Add("@Code", request.Code?.Trim().ToUpperInvariant());
        parameters.Add("@NameAr", request.NameAr);
        parameters.Add("@NameEn", request.NameEn);
        parameters.Add("@Phone", Blank(request.Phone));
        parameters.Add("@AddressAr", Blank(request.AddressAr));
        parameters.Add("@AddressEn", Blank(request.AddressEn));
        parameters.Add("@IsActive", request.IsActive ?? true);
        parameters.Add("@VatRatePercent", request.VatRatePercent ?? 15.00m);
        parameters.Add("@PricesIncludeVat", request.PricesIncludeVat ?? true);
        parameters.Add("@AllowNegativeStock", request.AllowNegativeStock ?? false);
        parameters.Add("@Actor", current.Username);
        parameters.Add("@ActorUserId", current.UserId);
        parameters.Add("@SavedId", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 40);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "core.usp_SaveBranch", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is >= 61000 and <= 61005)
        {
            return Results.UnprocessableEntity(WriteErrors.From(ex));
        }

        var branchId = parameters.Get<string>("@SavedId");
        http.Items["AuditEntityId"] = branchId;

        var recorder = ChangeRecorder.For(http);

        if (before is null)
        {
            /* A creation writes three tables. The audit names all three, so it
               is clear the settings and access were not an afterthought. */
            recorder
                .Inserted("core", "Branch", branchId)
                .Inserted("core", "BranchSettings", branchId, branchId, "core.Branch")
                .Inserted("core", "UserBranch", current.UserId, branchId, "core.Branch");
        }
        else
        {
            var was = (IDictionary<string, object>)before;
            recorder
                .Updated("core", "Branch", branchId, "Code", was["Code"], request.Code?.Trim().ToUpperInvariant())
                .Updated("core", "Branch", branchId, "NameAr", was["NameAr"], request.NameAr)
                .Updated("core", "Branch", branchId, "NameEn", was["NameEn"], request.NameEn)
                .Updated("core", "Branch", branchId, "Phone", was["Phone"], Blank(request.Phone))
                .Updated("core", "Branch", branchId, "IsActive", was["IsActive"], request.IsActive ?? true)
                .Updated("core", "BranchSettings", branchId, "VatRatePercent",
                         was["VatRatePercent"], request.VatRatePercent ?? 15.00m)
                .Updated("core", "BranchSettings", branchId, "PricesIncludeVat",
                         was["PricesIncludeVat"], request.PricesIncludeVat ?? true)
                .Updated("core", "BranchSettings", branchId, "AllowNegativeStock",
                         was["AllowNegativeStock"], request.AllowNegativeStock ?? false);
        }

        return request.BranchId is null
            ? Results.Created($"/api/branches/{branchId}", new { branchId })
            : Results.Ok(new { branchId });
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record SaveBranchRequest
{
    public string? BranchId { get; init; }
    public string? Code { get; init; }
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? AddressAr { get; init; }
    public string? AddressEn { get; init; }
    public bool? IsActive { get; init; }
    public decimal? VatRatePercent { get; init; }
    public bool? PricesIncludeVat { get; init; }
    public bool? AllowNegativeStock { get; init; }
}
