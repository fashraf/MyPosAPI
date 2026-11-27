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
/// Returns. The till works out the lines and amounts (same rules as the sale); the server checks nothing is returned
/// twice, assigns the credit note number (500001+), ties a cash refund to the open day, puts stock back and marks the
/// invoice returned or partly returned, all in one transaction.
/// </summary>
public static class CreditNoteEndpoints
{
    public sealed record ReturnBody(JsonObject Note, string? IdempotencyKey);

    public static void MapCreditNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sales").WithTags("Returns").RequireRateLimiting("standard");
        g.MapPost("/{saleId:long}/returns", IssueAsync).RequireAuthorization(PermissionPolicy.For("sales.refund"));
        g.MapGet("/credit-notes", ListAsync).RequireAuthorization(PermissionPolicy.For("sales.view"));
    }

    private static async Task<IResult> ListAsync(IDb db, DateOnly? from, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var since = (from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90))).ToDateTime(TimeOnly.MinValue);
        var rows = await db.Query(conn).Query("sales.credit_note").Where("issued_at_utc", ">=", since)
            .OrderByDesc("note_no").Limit(5000).GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Cast<IDictionary<string, object?>>().Select(Doc));
    }

    private static async Task<IResult> IssueAsync(long saleId, ReturnBody body, IDb db, INumberSeries numbers, ICurrentUser user, CancellationToken ct)
    {
        var note = body.Note;
        var refund = TenantSeeder.S(note, "refundMethod") ?? "cash";
        var reason = TenantSeeder.S(note, "reason") ?? "other";
        if (note["lines"] is not JsonArray lines || lines.Count == 0)
            return Error(422, "nothing_to_return", "Choose at least one item to return.", "اختر صنفًا واحدًا على الأقل للإرجاع.");
        try
        {
            var result = await db.InTransactionAsync(async (conn, tx) =>
            {
                var q = db.Query(conn);
                if (!string.IsNullOrWhiteSpace(body.IdempotencyKey))
                {
                    var replay = (await q.Query("sales.credit_note").Where("idempotency_key", body.IdempotencyKey).GetAsync(tx))
                        .Cast<IDictionary<string, object?>>().FirstOrDefault();
                    if (replay is not null) return Doc(replay);
                }
                var saleRow = (await q.Query("sales.sale").Where("sale_id", saleId).GetAsync(tx)).Cast<IDictionary<string, object?>>().FirstOrDefault()
                    ?? throw new Rule(404, "not_found", "That sale was not found.", "لم يتم العثور على الفاتورة.");
                if ((string)saleRow["status"]! == "voided")
                    throw new Rule(409, "sale_voided", "A cancelled sale has nothing left to return.", "لا يوجد ما يُرجع من فاتورة ملغاة.");
                var sale = CatalogEndpoints.Parse(saleRow["document_json"]) ?? new JsonObject();
                var branchId = Convert.ToInt64(saleRow["branch_id"]);

                /* What is left to return, per sale line, after every earlier credit note. */
                var sold = (sale["lines"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .ToDictionary(l => TenantSeeder.S(l, "id") ?? "", l => l);
                var already = new Dictionary<string, decimal>();
                foreach (var prev in (await q.Query("sales.credit_note").Where("sale_id", saleId).Select("document_json").GetAsync(tx)).Cast<IDictionary<string, object?>>())
                    foreach (var pl in (CatalogEndpoints.Parse(prev["document_json"])?["lines"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        var k = TenantSeeder.S(pl, "saleLineId") ?? "";
                        already[k] = already.GetValueOrDefault(k) + Dec(pl, "quantity");
                    }
                foreach (var line in lines.OfType<JsonObject>())
                {
                    var key = TenantSeeder.S(line, "saleLineId") ?? "";
                    if (!sold.TryGetValue(key, out var s))
                        throw new Rule(422, "unknown_line", "That line is not part of this invoice.", "هذا السطر ليس من هذه الفاتورة.");
                    if (Dec(line, "quantity") <= 0 || Dec(line, "quantity") + already.GetValueOrDefault(key) > Dec(s, "quantity"))
                        throw new Rule(422, "over_return", "You cannot return more than was sold and not already returned.", "لا يمكن إرجاع أكثر مما بيع ولم يُرجع.");
                }

                /* A cash refund comes out of today's drawer, so the restaurant must be open. */
                object? open = await q.Query("sales.restaurant_session").Where("branch_id", branchId).Where("status", "open").Select("session_id").FirstOrDefaultAsync(tx);
                long? sessionId = open is null ? null : Convert.ToInt64(((IDictionary<string, object?>)open)["session_id"]);
                if (refund == "cash" && sessionId is null)
                    throw new Rule(409, "restaurant_closed", "Open the restaurant to give a cash refund.", "افتح المطعم لإرجاع مبلغ نقدي.");

                var noteNo = await numbers.NextAsync(conn, tx, "credit_note");
                var now = DateTime.UtcNow;
                var iso = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                var doc = (JsonObject)note.DeepClone();
                doc["id"] = noteNo.ToString(CultureInfo.InvariantCulture); doc["noteNumber"] = noteNo.ToString(CultureInfo.InvariantCulture);
                doc["saleId"] = saleId.ToString(CultureInfo.InvariantCulture); doc["saleInvoiceNumber"] = saleId.ToString(CultureInfo.InvariantCulture);
                doc["sessionId"] = sessionId?.ToString(CultureInfo.InvariantCulture); doc["issuedAt"] = iso; doc["createdAt"] = iso; doc["updatedAt"] = iso;
                doc["issuedBy"] = TenantSeeder.S(note, "issuedBy") ?? user.Username;

                await q.Query("sales.credit_note").InsertAsync(new Dictionary<string, object?>
                {
                    ["note_no"] = noteNo, ["sale_id"] = saleId, ["session_id"] = sessionId, ["branch_id"] = branchId, ["refund_method"] = refund,
                    ["subtotal_h"] = L(note, "subtotalH"), ["tax_h"] = L(note, "taxH"), ["total_h"] = L(note, "totalH"), ["reason"] = reason,
                    ["issued_by"] = TenantSeeder.S(doc, "issuedBy"), ["idempotency_key"] = body.IdempotencyKey,
                    ["document_json"] = doc.ToJsonString(), ["issued_at_utc"] = now,
                }, tx);

                /* Returned goods go back on the shelf (items that track stock). */
                foreach (var line in lines.OfType<JsonObject>())
                {
                    var s = sold[TenantSeeder.S(line, "saleLineId")!];
                    if (!long.TryParse(TenantSeeder.S(s, "itemId"), out var itemId)) continue;
                    var item = (await q.Query("core.catalog_item").Where("item_id", itemId).Where("track_inventory", true).Select("stock_quantity").GetAsync(tx))
                        .Cast<IDictionary<string, object?>>().FirstOrDefault();
                    if (item is null) continue;
                    await q.Query("core.catalog_item").Where("item_id", itemId).UpdateAsync(new Dictionary<string, object?>
                        { ["stock_quantity"] = Convert.ToDecimal(item["stock_quantity"]) + Dec(line, "quantity"), ["updated_at_utc"] = now }, tx);
                }

                foreach (var line in lines.OfType<JsonObject>())
                {
                    var k = TenantSeeder.S(line, "saleLineId")!;
                    already[k] = already.GetValueOrDefault(k) + Dec(line, "quantity");
                }
                var fully = sold.All(kv => already.GetValueOrDefault(kv.Key) >= Dec(kv.Value, "quantity"));
                var status = fully ? "returned" : "partially_returned";
                sale["status"] = status; sale["updatedAt"] = iso;
                await q.Query("sales.sale").Where("sale_id", saleId).UpdateAsync(new Dictionary<string, object?>
                    { ["status"] = status, ["document_json"] = sale.ToJsonString(), ["updated_at_utc"] = now }, tx);
                return doc;
            }, ct);
            return Results.Ok(result);
        }
        catch (Rule r)
        {
            return Error(r.Status, r.Code, r.Message, r.MessageAr);
        }
    }

    internal static JsonObject Doc(IDictionary<string, object?> r) => CatalogEndpoints.Parse(r["document_json"]) ?? new JsonObject();

    private static decimal Dec(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : 0m;
    private static long L(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var d) ? d : 0L;

    private static IResult Error(int status, string code, string en, string ar) => Results.Json(ApiError.Create(code, en, ar), statusCode: status);

    private sealed class Rule(int status, string code, string message, string messageAr) : Exception(message)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
        public string MessageAr { get; } = messageAr;
    }
}
