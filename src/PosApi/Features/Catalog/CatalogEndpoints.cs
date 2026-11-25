using System.Text.Json.Nodes;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;
using PosApi.Data.Seeding;
using SqlKata.Execution;

namespace PosApi.Features.Catalog;

/// <summary>
/// The catalog on the engine-neutral layer. An item is returned whole: everything the front end saved (modifier
/// groups, combo slots, recipe, description) plus the authoritative columns (price, stock, status) laid over it.
/// The older flat fields (itemId, name, quantity, tracksStock, reorderLevel, isActive) are still included, so the
/// previous client keeps working.
/// </summary>
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/catalog").WithTags("Catalog").RequireAuthorization();

        g.MapGet("/items", ListAsync).RequireAuthorization(PermissionPolicy.For("products.view"));
        g.MapGet("/items/{itemId}", GetAsync).RequireAuthorization(PermissionPolicy.For("products.view"));
        g.MapPost("/items", (JsonObject body, IDb db, ICurrentUser user, CancellationToken ct) => SaveAsync(null, body, db, user, ct))
            .RequireAuthorization(PermissionPolicy.For("products.create"));
        g.MapPut("/items/{itemId}", (string itemId, JsonObject body, IDb db, ICurrentUser user, CancellationToken ct) => SaveAsync(itemId, body, db, user, ct))
            .RequireAuthorization(PermissionPolicy.For("products.edit"));
        g.MapGet("/categories", CategoriesAsync).RequireAuthorization(PermissionPolicy.For("products.view"));
        g.MapPut("/categories/{categoryId}", SaveCategoryAsync).RequireAuthorization(PermissionPolicy.For("products.edit"));
        g.MapPost("/categories", (JsonObject body, IDb db, CancellationToken ct) => SaveCategoryAsync(TenantSeeder.S(body, "id") ?? $"cat_{Guid.NewGuid():N}"[..16], body, db, ct))
            .RequireAuthorization(PermissionPolicy.For("products.create"));
        g.MapGet("/print-groups", PrintGroupsAsync).RequireAuthorization(PermissionPolicy.For("products.view"));
    }

    private static async Task<IResult> ListAsync(
        IDb db, string? search, string? categoryId, string? kind, bool? activeOnly,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var paging = new PageQuery { Page = page <= 0 ? 1 : page, PageSize = Math.Clamp(pageSize, 1, 2000) };
        await using var conn = await db.OpenAsync(ct);
        var q = db.Query(conn);

        SqlKata.Query Filtered()
        {
            var x = q.Query("core.catalog_item");
            if (activeOnly ?? true) x.Where("status", "active");
            if (!string.IsNullOrWhiteSpace(categoryId)) x.Where("category_id", categoryId);
            if (!string.IsNullOrWhiteSpace(kind)) x.Where("kind", kind);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                x.Where(w => w.WhereLike("name_en", $"%{s}%").OrWhereLike("name_ar", $"%{s}%").OrWhere("sku", s).OrWhere("barcode", s));
            }
            return x;
        }

        var total = await Filtered().CountAsync<int>(cancellationToken: ct);
        var rows = await Filtered().OrderBy("kind", "name_en").Offset(paging.Offset).Limit(paging.PageSize).GetAsync(cancellationToken: ct);
        return Results.Ok(new PagedResult<JsonObject>
        {
            Items = rows.Select(r => CatalogRow.ToDocument((IDictionary<string, object?>)r)).ToList(),
            Page = paging.Page, PageSize = paging.PageSize, TotalCount = total,
        });
    }

    private static async Task<IResult> GetAsync(string itemId, IDb db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        object? row = await db.Query(conn).Query("core.catalog_item").Where("item_id", itemId).FirstOrDefaultAsync(cancellationToken: ct);
        return row is null ? Results.NotFound() : Results.Ok(CatalogRow.ToDocument((IDictionary<string, object?>)row));
    }

    /// <summary>Create or replace an item with the whole document the editor produced.</summary>
    private static async Task<IResult> SaveAsync(string? itemId, JsonObject body, IDb db, ICurrentUser user, CancellationToken ct)
    {
        var id = itemId ?? TenantSeeder.S(body, "id") ?? $"itm_{Guid.NewGuid():N}"[..16];
        body["id"] = id;
        if (TenantSeeder.S(body, "nameEn") is null && TenantSeeder.S(body, "nameAr") is null)
            return Results.Json(ApiError.Create("name_required", "Give the item a name.", "أدخل اسم الصنف."), statusCode: 422);
        if (body["priceH"] is not JsonValue p || !p.TryGetValue<double>(out var price) || price < 0)
            return Results.Json(ApiError.Create("bad_price", "The price must be zero or more.", "يجب أن يكون السعر صفرًا أو أكثر."), statusCode: 422);

        /* Stock moves through sales and stock adjustments only: an edit to the name or price must not overwrite a
           quantity a sale changed a moment ago. An adjustment says so with setStock. */
        var setStock = body["setStock"] is JsonValue ss && ss.TryGetValue<bool>(out var flag) && flag;
        body.Remove("setStock");
        var now = DateTime.UtcNow;
        var row = CatalogRow.From(body, now);
        await db.InTransactionAsync(async (conn, tx) =>
        {
            var q = db.Query(conn);
            var exists = await q.Query("core.catalog_item").Where("item_id", id).ExistsAsync(tx);
            if (exists)
            {
                row.Remove("item_id"); row.Remove("created_at_utc");
                if (!setStock) row.Remove("stock_quantity");
                await q.Query("core.catalog_item").Where("item_id", id).UpdateAsync(row, tx);
            }
            else await q.Query("core.catalog_item").InsertAsync(row, tx);
            return 0;
        }, ct);
        return await GetAsync(id, db, ct);
    }

    private static async Task<IResult> CategoriesAsync(IDb db, bool? activeOnly, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var x = db.Query(conn).Query("core.category").OrderBy("sort_order", "name_en");
        if (activeOnly ?? true) x.Where("is_active", true);
        var rows = await x.GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Select(r =>
        {
            var d = (IDictionary<string, object?>)r;
            var doc = Parse(d["details_json"]) ?? new JsonObject();
            doc["id"] = (string)d["category_id"]!; doc["nameAr"] = (string)d["name_ar"]!; doc["nameEn"] = (string)d["name_en"]!;
            doc["name"] = (string)d["name_en"]!; doc["sortOrder"] = Convert.ToInt32(d["sort_order"]);
            doc["status"] = Convert.ToBoolean(d["is_active"]) ? "active" : "inactive";
            return doc;
        }));
    }

    private static async Task<IResult> SaveCategoryAsync(string categoryId, JsonObject body, IDb db, CancellationToken ct)
    {
        body["id"] = categoryId;
        var now = DateTime.UtcNow;
        var row = new Dictionary<string, object?>
        {
            ["name_ar"] = TenantSeeder.S(body, "nameAr") ?? TenantSeeder.S(body, "nameEn") ?? "",
            ["name_en"] = TenantSeeder.S(body, "nameEn") ?? TenantSeeder.S(body, "nameAr") ?? "",
            ["applies_to"] = TenantSeeder.S(body, "appliesTo"), ["sort_order"] = TenantSeeder.I(body, "sortOrder"),
            ["is_active"] = TenantSeeder.S(body, "status") != "inactive", ["details_json"] = body.ToJsonString(), ["updated_at_utc"] = now,
        };
        await db.InTransactionAsync(async (conn, tx) =>
        {
            var q = db.Query(conn);
            if (await q.Query("core.category").Where("category_id", categoryId).ExistsAsync(tx))
                await q.Query("core.category").Where("category_id", categoryId).UpdateAsync(row, tx);
            else
            {
                row["category_id"] = categoryId; row["created_at_utc"] = now;
                await q.Query("core.category").InsertAsync(row, tx);
            }
            return 0;
        }, ct);
        return Results.Ok(body);
    }

    private static async Task<IResult> PrintGroupsAsync(IDb db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await db.Query(conn).Query("core.print_group").Where("is_active", true).OrderBy("sort_order").GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Select(r =>
        {
            var d = (IDictionary<string, object?>)r;
            return new
            {
                id = d["print_group_id"], nameAr = d["name_ar"], nameEn = d["name_en"], name = d["name_en"],
                ticketTitle = d["ticket_title"], sortOrder = d["sort_order"], status = "active",
            };
        }));
    }

    internal static JsonObject? Parse(object? json) =>
        json is string s && !string.IsNullOrWhiteSpace(s) ? JsonNode.Parse(s) as JsonObject : null;
}

/// <summary>Maps a catalog item document to its row and back.</summary>
public static class CatalogRow
{
    public static Dictionary<string, object?> From(JsonObject item, DateTime now)
    {
        string? S(string k) => TenantSeeder.S(item, k);
        bool B(string k, bool fallback) => item[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
        decimal D(string k) => item[k] is JsonValue v && v.TryGetValue<double>(out var d) ? (decimal)d : 0m;
        long L(string k) => item[k] is JsonValue v && v.TryGetValue<double>(out var d) ? (long)Math.Round(d) : 0L;
        var isCombo = item["combo"] is JsonObject c && c["slots"] is JsonArray slots && slots.Count > 0;

        return new Dictionary<string, object?>
        {
            ["item_id"] = S("id"), ["kind"] = S("kind") ?? "product",
            ["name_ar"] = S("nameAr") ?? S("nameEn") ?? "", ["name_en"] = S("nameEn") ?? S("nameAr") ?? "",
            ["category_id"] = S("categoryId"), ["print_group_id"] = S("printGroupId"),
            ["sku"] = S("sku"), ["barcode"] = S("barcode"), ["price_h"] = L("priceH"), ["cost_h"] = L("costH"),
            ["tax_rate_id"] = S("taxRateId"), ["show_on_pos"] = B("showOnPos", true),
            ["requires_preparation"] = B("requiresPreparation", false), ["track_inventory"] = B("trackInventory", false),
            ["stock_quantity"] = D("stockQuantity"), ["min_stock_level"] = D("minStockLevel"), ["unit"] = S("unit"),
            ["is_combo"] = isCombo, ["status"] = S("status") ?? "active",
            ["details_json"] = item.ToJsonString(), ["created_at_utc"] = now, ["updated_at_utc"] = now,
        };
    }

    public static JsonObject ToDocument(IDictionary<string, object?> r)
    {
        var doc = CatalogEndpoints.Parse(r["details_json"]) ?? new JsonObject();
        var id = (string)r["item_id"]!;
        var nameEn = (string)r["name_en"]!;
        var active = (string)r["status"]! == "active";
        var stock = Convert.ToDecimal(r["stock_quantity"]);
        /* Columns win: price, stock and status may have changed since the document was saved (a sale moves stock). */
        doc["id"] = id; doc["kind"] = (string)r["kind"]!;
        doc["nameAr"] = (string)r["name_ar"]!; doc["nameEn"] = nameEn;
        doc["categoryId"] = r["category_id"] as string; doc["printGroupId"] = r["print_group_id"] as string;
        doc["priceH"] = Convert.ToInt64(r["price_h"]); doc["costH"] = Convert.ToInt64(r["cost_h"]);
        doc["status"] = (string)r["status"]!; doc["showOnPos"] = Convert.ToBoolean(r["show_on_pos"]);
        doc["requiresPreparation"] = Convert.ToBoolean(r["requires_preparation"]);
        if ((string)r["kind"]! == "product")
        {
            doc["trackInventory"] = Convert.ToBoolean(r["track_inventory"]);
            doc["stockQuantity"] = (double)stock;
            doc["minStockLevel"] = (double)Convert.ToDecimal(r["min_stock_level"]);
            doc["sku"] = r["sku"] as string ?? ""; doc["barcode"] = r["barcode"] as string ?? "";
        }
        /* Older flat fields, kept for the previous client. */
        doc["itemId"] = id; doc["name"] = nameEn; doc["quantity"] = (double)stock;
        doc["tracksStock"] = Convert.ToBoolean(r["track_inventory"]);
        doc["reorderLevel"] = (int)Convert.ToDecimal(r["min_stock_level"]); doc["isActive"] = active;
        return doc;
    }
}
