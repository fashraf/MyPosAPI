using System.Text.Json.Nodes;
using PosApi.Common;
using PosApi.Data;
using PosApi.Data.Seeding;
using PosApi.Features.Catalog;
using SqlKata.Execution;

namespace PosApi.Features.Kitchen;

/// <summary>
/// Kitchen tickets, shared by every device. The kitchen screen's logic (statuses, item progress, routing) stays in the
/// front end; the server keeps the latest version of each ticket, keyed by its order's invoice number.
/// </summary>
public static class KitchenTicketEndpoints
{
    public static void MapKitchenTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/kitchen/tickets").WithTags("Kitchen").RequireAuthorization().RequireRateLimiting("standard");

        /* Recent tickets (default: the last 2 days), newest first. */
        g.MapGet("", async (IDb db, int? hours, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 48, 1, 24 * 31));
            var rows = await db.Query(conn).Query("sales.kitchen_ticket").Where("updated_at_utc", ">=", since)
                .OrderByDesc("created_at_utc").Limit(1000).GetAsync(cancellationToken: ct);
            return Results.Ok(rows.Select(r => Doc((IDictionary<string, object?>)r)));
        });

        /* Create or update one ticket with the whole ticket the device holds. */
        g.MapPut("/{invoiceNo:long}", async (long invoiceNo, JsonObject body, IDb db, CancellationToken ct) =>
        {
            var status = TenantSeeder.S(body, "status") ?? "open";
            if (status.Length > 20)
                return Results.Json(ApiError.Create("bad_status", "Unknown ticket status.", "حالة طلب المطبخ غير معروفة."), statusCode: 422);
            var now = DateTime.UtcNow;
            body["id"] = invoiceNo.ToString();
            body["orderNumber"] = invoiceNo.ToString();
            var outcome = await db.InTransactionAsync(async (conn, tx) =>
            {
                var q = db.Query(conn);
                if (!await q.Query("sales.sale").Where("invoice_no", invoiceNo).ExistsAsync(tx)) return 404;
                var row = new Dictionary<string, object?> { ["status"] = status, ["document_json"] = body.ToJsonString(), ["updated_at_utc"] = now };
                if (await q.Query("sales.kitchen_ticket").Where("invoice_no", invoiceNo).ExistsAsync(tx))
                    await q.Query("sales.kitchen_ticket").Where("invoice_no", invoiceNo).UpdateAsync(row, tx);
                else
                {
                    row["invoice_no"] = invoiceNo; row["created_at_utc"] = now;
                    await q.Query("sales.kitchen_ticket").InsertAsync(row, tx);
                }
                return 200;
            }, ct);
            return outcome == 404
                ? Results.Json(ApiError.Create("not_found", "There is no order with that number.", "لا يوجد طلب بهذا الرقم."), statusCode: 404)
                : Results.Ok(body);
        });
    }

    private static JsonObject Doc(IDictionary<string, object?> r)
    {
        var doc = CatalogEndpoints.Parse(r["document_json"]) ?? new JsonObject();
        var no = Convert.ToInt64(r["invoice_no"]).ToString();
        doc["id"] = no; doc["orderNumber"] = no; doc["status"] = (string)r["status"]!;
        return doc;
    }
}
