using Dapper;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;

namespace PosApi.Features.Catalog;

/// <summary>
/// Creating and editing catalogue records.
///
/// These were missing, so the catalogue screen saved into memory and lost the
/// change on reload. One handler covers create and update: the difference is
/// whether an id was supplied, and two handlers would mean two places for the
/// same validation to drift apart.
/// </summary>
public static class CatalogWriteEndpoints
{
    public static void MapCatalogWriteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog")
            .WithTags("Catalog")
            .RequireAuthorization()
            .RequireRateLimiting("standard");

        group.MapPost("/items", (SaveItemRequest r, ISqlConnectionFactory db,
                                 ICurrentUser u, HttpContext h, CancellationToken ct)
                 => SaveItemAsync(r with { ItemId = null }, db, u, h, ct))
             .RequireAuthorization(PermissionPolicy.For("products.create"));

        group.MapPut("/items/{itemId}", (string itemId, SaveItemRequest r,
                                         ISqlConnectionFactory db, ICurrentUser u,
                                         HttpContext h, CancellationToken ct)
                 => SaveItemAsync(r with { ItemId = itemId }, db, u, h, ct))
             .RequireAuthorization(PermissionPolicy.For("products.edit"));

        group.MapPost("/categories", (SaveCategoryRequest r, ISqlConnectionFactory db,
                                      HttpContext h, CancellationToken ct)
                 => SaveCategoryAsync(r with { CategoryId = null }, db, h, ct))
             .RequireAuthorization(PermissionPolicy.For("products.create"));

        group.MapPut("/categories/{categoryId}", (string categoryId, SaveCategoryRequest r,
                                                  ISqlConnectionFactory db, HttpContext h,
                                                  CancellationToken ct)
                 => SaveCategoryAsync(r with { CategoryId = categoryId }, db, h, ct))
             .RequireAuthorization(PermissionPolicy.For("products.edit"));
    }

    private static async Task<IResult> SaveItemAsync(
        SaveItemRequest request, ISqlConnectionFactory db, ICurrentUser current,
        HttpContext http, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@ItemId", request.ItemId);
        parameters.Add("@Kind", request.Kind ?? "product");
        parameters.Add("@NameAr", request.NameAr);
        parameters.Add("@NameEn", request.NameEn);
        parameters.Add("@DescriptionAr", request.DescriptionAr);
        parameters.Add("@DescriptionEn", request.DescriptionEn);
        parameters.Add("@Sku", Blank(request.Sku));
        parameters.Add("@Barcode", Blank(request.Barcode));
        parameters.Add("@CategoryId", Blank(request.CategoryId));
        parameters.Add("@VendorId", Blank(request.VendorId));
        parameters.Add("@PrintGroupId", Blank(request.PrintGroupId));
        parameters.Add("@PriceH", request.PriceH);
        parameters.Add("@CostH", request.CostH ?? 0);
        parameters.Add("@TracksStock", request.TracksStock ?? true);
        parameters.Add("@ReorderLevel", request.ReorderLevel ?? 0);
        parameters.Add("@RequiresPreparation", request.RequiresPreparation ?? false);
        parameters.Add("@IsActive", request.IsActive ?? true);
        parameters.Add("@Actor", current.Username);
        parameters.Add("@SavedItemId", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 40);

        await using var connection = db.Create();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "core.usp_SaveCatalogItem", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is >= 59000 and <= 59005)
        {
            /* The procedure's own wording. It names the field and what is wrong
               with it, which a generic "invalid input" cannot. */
            return Results.UnprocessableEntity(WriteErrors.From(ex));
        }

        var itemId = parameters.Get<string>("@SavedItemId");
        http.Items["AuditEntityId"] = itemId;

        return request.ItemId is null
            ? Results.Created($"/api/catalog/items/{itemId}", new { itemId })
            : Results.Ok(new { itemId });
    }

    private static async Task<IResult> SaveCategoryAsync(
        SaveCategoryRequest request, ISqlConnectionFactory db,
        HttpContext http, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@CategoryId", request.CategoryId);
        parameters.Add("@NameAr", request.NameAr);
        parameters.Add("@NameEn", request.NameEn);
        parameters.Add("@SortOrder", request.SortOrder ?? 0);
        parameters.Add("@IsActive", request.IsActive ?? true);
        parameters.Add("@SavedId", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 40);

        await using var connection = db.Create();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "core.usp_SaveCategory", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is >= 59010 and <= 59011)
        {
            return Results.UnprocessableEntity(WriteErrors.From(ex));
        }

        var categoryId = parameters.Get<string>("@SavedId");
        http.Items["AuditEntityId"] = categoryId;

        return Results.Ok(new { categoryId });
    }

    /// <summary>
    /// Empty becomes null.
    ///
    /// A blank SKU submitted as "" would collide with every other blank SKU on
    /// the uniqueness check, so the second item saved without one would be
    /// refused for a reason nobody could see.
    /// </summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record SaveItemRequest
{
    public string? ItemId { get; init; }
    public string? Kind { get; init; }
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string? DescriptionAr { get; init; }
    public string? DescriptionEn { get; init; }
    public string? Sku { get; init; }
    public string? Barcode { get; init; }
    public string? CategoryId { get; init; }
    public string? VendorId { get; init; }
    public string? PrintGroupId { get; init; }
    public long PriceH { get; init; }
    public long? CostH { get; init; }
    public bool? TracksStock { get; init; }
    public decimal? ReorderLevel { get; init; }
    public bool? RequiresPreparation { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record SaveCategoryRequest
{
    public string? CategoryId { get; init; }
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public int? SortOrder { get; init; }
    public bool? IsActive { get; init; }
}
