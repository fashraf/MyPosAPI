using System.Globalization;
using System.Text.Json.Nodes;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;
using PosApi.Data.Seeding;
using PosApi.Features.Catalog;
using SqlKata.Execution;

namespace PosApi.Features.Sales;

/// <summary>
/// Sales on the engine-neutral layer.
///
/// POST /api/sales takes the sale the till built (lines with their options and combo parts, payments, totals) and
/// commits it in ONE transaction: the restaurant must be open for the branch; the invoice number (100001, 100002 …)
/// comes from the number series; the order number restarts at 1 for each business day; the ZATCA stamp chains to the
/// previous invoice of the branch's EGS unit; stock is reduced for items that track it. Any failure rolls back all of it.
/// A retry with the same idempotency key returns the original sale instead of a second one.
/// </summary>
public static class SalesEndpoints
{
    public static void MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sales").WithTags("Sales").RequireAuthorization();

        g.MapPost("", CommitAsync).RequireAuthorization(PermissionPolicy.For("pos.create_sale"));
        g.MapGet("", ListAsync).RequireAuthorization(PermissionPolicy.For("sales.view"));
        g.MapGet("/{saleId:long}", GetAsync).RequireAuthorization(PermissionPolicy.For("sales.view"));
        g.MapPost("/{saleId:long}/void", VoidAsync).RequireAuthorization(PermissionPolicy.For("sales.void"));
    }

    public sealed record CommitRequest(JsonObject Sale, string? IdempotencyKey, List<StockMove>? StockMovements);
    public sealed record StockMove(string ItemId, decimal Quantity);
    public sealed record VoidRequest(string Reason, string? Code, string? VoidedBy, string? ReplacedByInvoice = null);

    private static async Task<IResult> CommitAsync(CommitRequest body, IDb db, INumberSeries numbers, ICurrentUser user, PosApi.Features.OrderLight.OrderFeed feed, CancellationToken ct)
    {
        var sale = body.Sale;
        var key = body.IdempotencyKey ?? TenantSeeder.S(sale, "idempotencyKey");
        var branchId = TenantSeeder.S(sale, "branchId");
        var lines = TenantSeeder.Arr(sale, "lines").OfType<JsonObject>().ToList();
        var payments = TenantSeeder.Arr(sale, "payments").OfType<JsonObject>().ToList();
        long L(JsonNode? n, string k) => n?[k] is JsonValue v && v.TryGetValue<double>(out var d) ? (long)Math.Round(d) : 0;
        decimal D(JsonNode? n, string k) => n?[k] is JsonValue v && v.TryGetValue<double>(out var d) ? (decimal)d : 0m;

        /* ---- checks that need no database */
        if (string.IsNullOrWhiteSpace(branchId)) return Error(422, "branch_required", "The sale has no branch.", "لا يوجد فرع للفاتورة.");
        if (!user.CanSeeBranch(branchId)) return Error(403, "branch_forbidden", "You do not have access to this branch.", "ليست لديك صلاحية على هذا الفرع.");
        if (lines.Count == 0) return Error(422, "empty_cart", "Add at least one item before taking payment.", "أضف صنفًا واحدًا على الأقل قبل الدفع.");
        if (lines.Any(l => D(l, "quantity") <= 0)) return Error(422, "bad_quantity", "Every line needs a quantity above zero.", "يجب أن تكون كمية كل سطر أكبر من صفر.");
        var subtotal = L(sale, "subtotalH"); var tax = L(sale, "taxH"); var total = L(sale, "totalH"); var discount = L(sale, "discountH");
        if (total < 0 || tax < 0 || discount < 0) return Error(422, "bad_totals", "The totals are not valid.", "الإجماليات غير صحيحة.");
        if (Math.Abs(subtotal + tax - total) > 1) return Error(422, "bad_totals", "Subtotal plus VAT does not equal the total.", "المجموع مع الضريبة لا يساوي الإجمالي.");
        var paid = payments.Sum(p => L(p, "amountH"));
        if (paid < total) return Error(422, "insufficient_payment", "The amount paid is less than the total due.", "المبلغ المدفوع أقل من الإجمالي المستحق.");
        if (payments.Any(p => TenantSeeder.S(p, "method") == "credit") && TenantSeeder.S(sale, "customerId") is null)
            return Error(422, "customer_required", "A credit sale needs a customer on the invoice.", "البيع الآجل يحتاج إلى عميل على الفاتورة.");

        var created = false;
        try
        {
            var doc = await db.InTransactionAsync(async (conn, tx) =>
            {
                var q = db.Query(conn);
                created = false;

                /* Same attempt sent twice (a retry after a timeout): return the original. */
                if (!string.IsNullOrWhiteSpace(key))
                {
                    object? replay = await q.Query("sales.sale").Where("idempotency_key", key).FirstOrDefaultAsync(tx);
                    if (replay is not null) return SaleDoc(RestaurantSessionEndpoints.Row(replay));
                }

                /* No business day, no order. */
                var session = await RestaurantSessionEndpoints.OpenSession(q, branchId, null, tx)
                    ?? throw new RuleException(409, "restaurant_closed", "The restaurant is closed. Open it before taking orders.", "المطعم مغلق. افتحه قبل استقبال الطلبات.");
                var sessionId = Convert.ToInt64(session["session_id"]);
                var branchKey = Convert.ToInt64(session["branch_id"]);

                var invoiceNo = await numbers.NextAsync(conn, tx, "invoice");
                var orderNo = Convert.ToInt32(session["last_order_no"]) + 1;
                await q.Query("sales.restaurant_session").Where("session_id", sessionId)
                    .UpdateAsync(new Dictionary<string, object?> { ["last_order_no"] = orderNo, ["updated_at_utc"] = DateTime.UtcNow }, tx);

                /* ZATCA chain for this branch's EGS unit. */
                var egs = $"EGS-{branchId.ToUpperInvariant()}-01";
                var chain = await q.Query("sales.zatca_chain").Where("egs_unit_id", egs).FirstOrDefaultAsync(tx);
                var icv = chain is null ? 1 : Convert.ToInt64(((IDictionary<string, object?>)chain)["icv"]) + 1;
                var pih = chain is null ? Zatca.GenesisPih : (string)((IDictionary<string, object?>)chain)["last_hash"]!;
                var company = await q.Query("core.company").OrderBy("company_id").FirstOrDefaultAsync(tx) as IDictionary<string, object?>;
                var seller = company?["name_ar"] as string ?? company?["name_en"] as string ?? "MyPOS";
                var vat = company?["vat_number"] as string ?? "";
                var now = DateTime.UtcNow;
                var issuedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                var uuid = Guid.NewGuid().ToString();
                var invoiceNumber = invoiceNo.ToString(CultureInfo.InvariantCulture);
                var (hash, qr) = Zatca.Stamp(uuid, icv, pih, invoiceNumber, issuedAt, seller, vat, total, tax,
                    lines.Select(l => new Zatca.StampLine(TenantSeeder.S(l, "itemId") ?? "", D(l, "quantity"), L(l, "unitPriceH"), L(l, "discountH"))));
                if (chain is null)
                    await q.Query("sales.zatca_chain").InsertAsync(new Dictionary<string, object?> { ["egs_unit_id"] = egs, ["icv"] = icv, ["last_hash"] = hash, ["updated_at_utc"] = now }, tx);
                else
                    await q.Query("sales.zatca_chain").Where("egs_unit_id", egs).UpdateAsync(new Dictionary<string, object?> { ["icv"] = icv, ["last_hash"] = hash, ["updated_at_utc"] = now }, tx);

                /* The document the till keeps, with the server's numbers and stamp. */
                /* The sale's key is its invoice number: unique, numeric, 1, 2, 3 … */
                var saleId = invoiceNo;
                var document = (JsonObject)sale.DeepClone();
                document["id"] = saleId.ToString(CultureInfo.InvariantCulture); document["invoiceNumber"] = invoiceNumber; document["orderNo"] = orderNo;
                document["sessionId"] = sessionId.ToString(CultureInfo.InvariantCulture); document["status"] = "completed"; document["soldAt"] = issuedAt;
                document["createdAt"] = issuedAt; document["updatedAt"] = issuedAt;
                var zatca = document["zatca"] as JsonObject ?? new JsonObject();
                zatca["uuid"] = uuid; zatca["icv"] = icv; zatca["invoiceHash"] = hash; zatca["previousInvoiceHash"] = pih;
                zatca["qrBase64"] = qr; zatca["egsUnitId"] = egs;
                zatca["invoiceType"] = TenantSeeder.S(sale, "customerId") is null ? "simplified" : "standard";
                zatca["reportingStatus"] ??= "pending";
                zatca["messages"] ??= new JsonArray();
                document["zatca"] = zatca;

                await q.Query("sales.sale").InsertAsync(new Dictionary<string, object?>
                {
                    ["sale_id"] = saleId, ["invoice_no"] = invoiceNo, ["order_no"] = orderNo, ["session_id"] = sessionId,
                    ["branch_id"] = branchKey, ["status"] = "completed", ["order_type"] = TenantSeeder.S(sale, "orderType"),
                    ["customer_id"] = TenantSeeder.S(sale, "customerId"), ["cashier_name"] = TenantSeeder.S(sale, "cashierName") ?? user.Username,
                    ["created_by_id"] = RestaurantSessionEndpoints.UserKey(user.UserId), ["item_count"] = lines.Sum(l => D(l, "quantity")),
                    ["subtotal_h"] = subtotal, ["discount_h"] = discount, ["tax_h"] = tax, ["total_h"] = total,
                    ["tendered_h"] = sale["tenderedH"] is JsonValue ? L(sale, "tenderedH") : null, ["change_h"] = L(sale, "changeH"),
                    ["idempotency_key"] = key, ["zatca_uuid"] = uuid, ["zatca_icv"] = icv, ["zatca_pih"] = pih,
                    ["zatca_hash"] = hash, ["zatca_qr"] = qr, ["sold_at_utc"] = now, ["document_json"] = document.ToJsonString(),
                    ["updated_at_utc"] = now,
                }, tx);

                var lineNo = 0;
                foreach (var l in lines)
                {
                    var qty = D(l, "quantity"); var unit = L(l, "unitPriceH"); var lineDiscount = L(l, "discountH");
                    var options = string.Join(", ",
                        TenantSeeder.Arr(l, "modifiers").Select(m => TenantSeeder.S(m, "nameEn")).Concat(
                        TenantSeeder.Arr(l, "components").Select(c => TenantSeeder.S(c, "nameEn"))).Where(x => x is not null));
                    await q.Query("sales.sale_line").InsertAsync(new Dictionary<string, object?>
                    {
                        ["sale_id"] = saleId, ["line_no"] = ++lineNo, ["item_id"] = ItemKey(TenantSeeder.S(l, "itemId")),
                        ["name_ar"] = TenantSeeder.S(l, "nameAr") ?? "", ["name_en"] = TenantSeeder.S(l, "nameEn") ?? "",
                        ["quantity"] = qty, ["unit_price_h"] = unit, ["unit_cost_h"] = L(l, "unitCostH"), ["discount_h"] = lineDiscount,
                        ["line_total_h"] = (long)Math.Round(unit * qty) - lineDiscount,
                        ["options_text"] = options.Length == 0 ? null : options[..Math.Min(options.Length, 1000)],
                        ["details_json"] = l.ToJsonString(),
                    }, tx);
                }

                var paymentNo = 0;
                foreach (var p in payments)
                    await q.Query("sales.sale_payment").InsertAsync(new Dictionary<string, object?>
                    {
                        ["sale_id"] = saleId, ["payment_no"] = ++paymentNo, ["method"] = TenantSeeder.S(p, "method") ?? "cash",
                        ["amount_h"] = L(p, "amountH"), ["reference"] = TenantSeeder.S(p, "reference"),
                    }, tx);

                /* Stock: the till sends what each line takes (a combo takes its parts); items that do not track stock are skipped. */
                var moves = body.StockMovements is { Count: > 0 }
                    ? body.StockMovements
                    : lines.Select(l => new StockMove(TenantSeeder.S(l, "itemId") ?? "", D(l, "quantity"))).ToList();
                foreach (var m in moves.GroupBy(m => m.ItemId).Select(g => new StockMove(g.Key, g.Sum(x => x.Quantity))))
                {
                    if (!long.TryParse(m.ItemId, out var stockItem) || m.Quantity <= 0) continue;
                    var item = await q.Query("core.catalog_item").Where("item_id", stockItem).Where("track_inventory", true)
                        .Select("stock_quantity").FirstOrDefaultAsync(tx);
                    if (item is null) continue;
                    var left = Convert.ToDecimal(((IDictionary<string, object?>)item)["stock_quantity"]) - m.Quantity;
                    await q.Query("core.catalog_item").Where("item_id", stockItem)
                        .UpdateAsync(new Dictionary<string, object?> { ["stock_quantity"] = left, ["updated_at_utc"] = now }, tx);
                }

                created = true;
                return document;
            }, ct);
            /* Announced only after the commit succeeded, and never for a replayed retry. */
            if (created)
                feed.Publish(long.Parse(doc["invoiceNumber"]!.ToString()), (int)doc["orderNo"]!, L(doc, "totalH"),
                    TenantSeeder.S(doc, "branchId") ?? "", TenantSeeder.S(doc, "orderType"));
            return Results.Ok(doc);
        }
        catch (RuleException r)
        {
            return Error(r.Status, r.Code, r.Message, r.MessageAr);
        }
    }

    private static async Task<IResult> ListAsync(
        IDb db, ICurrentUser user, string? branchId, string? sessionId, string? search, DateOnly? from, DateOnly? to,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var paging = new PageQuery { Page = page <= 0 ? 1 : page, PageSize = Math.Clamp(pageSize, 1, 2000) };
        await using var conn = await db.OpenAsync(ct);
        SqlKata.Query Base()
        {
            var x = db.Query(conn).Query("sales.sale");
            if (long.TryParse(branchId, out var branch)) x.Where("branch_id", branch);
            if (long.TryParse(sessionId, out var session)) x.Where("session_id", session);
            if (from is { } f) x.Where("sold_at_utc", ">=", f.ToDateTime(TimeOnly.MinValue).AddHours(-3));
            if (to is { } t) x.Where("sold_at_utc", "<", t.AddDays(1).ToDateTime(TimeOnly.MinValue).AddHours(-3));
            if (!string.IsNullOrWhiteSpace(search) && long.TryParse(search.Trim(), out var n)) x.Where("invoice_no", n);
            return x;
        }
        var total = await Base().CountAsync<int>(cancellationToken: ct);
        var rows = await Base().OrderByDesc("sold_at_utc").Offset(paging.Offset).Limit(paging.PageSize).GetAsync(cancellationToken: ct);
        return Results.Ok(new PagedResult<JsonObject>
        {
            Items = rows.Select(RestaurantSessionEndpoints.Row).Select(SaleDoc).ToList(),
            Page = paging.Page, PageSize = paging.PageSize, TotalCount = total,
        });
    }

    private static async Task<IResult> GetAsync(long saleId, IDb db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        object? row = await db.Query(conn).Query("sales.sale").Where("sale_id", saleId).FirstOrDefaultAsync(cancellationToken: ct);
        return row is null
            ? Error(404, "not_found", "That sale was not found.", "لم يتم العثور على الفاتورة.")
            : Results.Ok(SaleDoc(RestaurantSessionEndpoints.Row(row)));
    }

    /// <summary>Cancel a sale: it stays on record (an issued invoice is never deleted) and its stock goes back.</summary>
    private static async Task<IResult> VoidAsync(long saleId, VoidRequest body, IDb db, ICurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Reason)) return Error(422, "reason_required", "Give a reason for cancelling.", "اذكر سبب الإلغاء.");
        var actor = string.IsNullOrWhiteSpace(body.VoidedBy) ? user.Username : body.VoidedBy!;
        try
        {
            var doc = await db.InTransactionAsync(async (conn, tx) =>
            {
                var q = db.Query(conn);
                object? raw = await q.Query("sales.sale").Where("sale_id", saleId).FirstOrDefaultAsync(tx)
                    ?? throw new RuleException(404, "not_found", "That sale was not found.", "لم يتم العثور على الفاتورة.");
                var row = RestaurantSessionEndpoints.Row(raw);
                if ((string)row["status"]! == "voided") return SaleDoc(row);
                var now = DateTime.UtcNow;
                var document = CatalogEndpoints.Parse(row["document_json"]) ?? new JsonObject();
                document["status"] = "voided"; document["voidReason"] = body.Reason; document["cancelReasonCode"] = body.Code;
                document["voidedBy"] = actor; document["voidedAt"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                /* An edit cancels the old invoice and names the new one, so every device shows "Edited → #n". */
                if (!string.IsNullOrWhiteSpace(body.ReplacedByInvoice)) document["replacedByInvoice"] = body.ReplacedByInvoice;
                await q.Query("sales.sale").Where("sale_id", saleId).UpdateAsync(new Dictionary<string, object?>
                {
                    ["status"] = "voided", ["void_reason"] = body.Reason, ["voided_at_utc"] = now, ["voided_by"] = actor,
                    ["document_json"] = document.ToJsonString(), ["updated_at_utc"] = now,
                }, tx);
                foreach (object line in await q.Query("sales.sale_line").Where("sale_id", saleId).GetAsync(tx))
                {
                    var l = RestaurantSessionEndpoints.Row(line);
                    var item = await q.Query("core.catalog_item").Where("item_id", Convert.ToInt64(l["item_id"])).Where("track_inventory", true)
                        .Select("stock_quantity").FirstOrDefaultAsync(tx);
                    if (item is null) continue;
                    await q.Query("core.catalog_item").Where("item_id", Convert.ToInt64(l["item_id"])).UpdateAsync(new Dictionary<string, object?>
                    {
                        ["stock_quantity"] = Convert.ToDecimal(((IDictionary<string, object?>)item)["stock_quantity"]) + Convert.ToDecimal(l["quantity"]),
                        ["updated_at_utc"] = now,
                    }, tx);
                }
                return SaleDoc(RestaurantSessionEndpoints.Row(await q.Query("sales.sale").Where("sale_id", saleId).FirstAsync(tx)));
            }, ct);
            return Results.Ok(doc);
        }
        catch (RuleException r)
        {
            return Error(r.Status, r.Code, r.Message, r.MessageAr);
        }
    }

    internal static JsonObject SaleDoc(IDictionary<string, object?> r)
    {
        var doc = CatalogEndpoints.Parse(r["document_json"]) ?? new JsonObject();
        /* Columns win over the stored document. */
        doc["id"] = Convert.ToInt64(r["sale_id"]).ToString(CultureInfo.InvariantCulture); doc["invoiceNumber"] = Convert.ToInt64(r["invoice_no"]).ToString(CultureInfo.InvariantCulture);
        doc["orderNo"] = Convert.ToInt32(r["order_no"]); doc["sessionId"] = Convert.ToInt64(r["session_id"]).ToString(CultureInfo.InvariantCulture);
        doc["branchId"] = Convert.ToInt64(r["branch_id"]).ToString(CultureInfo.InvariantCulture); doc["status"] = (string)r["status"]!;
        doc["totalH"] = Convert.ToInt64(r["total_h"]); doc["taxH"] = Convert.ToInt64(r["tax_h"]);
        doc["subtotalH"] = Convert.ToInt64(r["subtotal_h"]); doc["discountH"] = Convert.ToInt64(r["discount_h"]);
        return doc;
    }

    /// <summary>Item ids are integers; a line with an id that is not a number cannot be sold.</summary>
    private static long ItemKey(string? value) =>
        long.TryParse(value, out var id) ? id : throw new RuleException(422, "unknown_item", "One of the items is not in the catalog.", "أحد الأصناف غير موجود في القائمة.");

    private static IResult Error(int status, string code, string en, string ar) =>
        Results.Json(ApiError.Create(code, en, ar), statusCode: status);

    private sealed class RuleException(int status, string code, string message, string messageAr) : Exception(message)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
        public string MessageAr { get; } = messageAr;
    }
}
