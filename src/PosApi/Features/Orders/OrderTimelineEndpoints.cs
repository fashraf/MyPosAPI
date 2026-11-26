using System.Globalization;
using System.Text.Json.Nodes;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;
using PosApi.Features.Catalog;
using SqlKata.Execution;

namespace PosApi.Features.Orders;

/// <summary>
/// One order's story, oldest first: placed, printed, sent to the kitchen, started, ready, collected, cancelled or
/// edited. Built from the sale, its kitchen ticket and the order events, all in the database.
/// </summary>
public static class OrderTimelineEndpoints
{
    public sealed record EventBody(string Kind, string? Detail);

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
        { "print_receipt", "print_kitchen", "print_a4", "print_other" };

    public static void MapOrderTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/orders").WithTags("Orders").RequireAuthorization().RequireRateLimiting("standard");

        g.MapPost("/{invoiceNo:long}/events", async (long invoiceNo, EventBody body, IDb db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!Kinds.Contains(body.Kind))
                return Results.Json(ApiError.Create("bad_kind", "Unknown event.", "حدث غير معروف."), statusCode: 422);
            await using var conn = await db.OpenAsync(ct);
            var q = db.Query(conn);
            if (!await q.Query("sales.sale").Where("invoice_no", invoiceNo).ExistsAsync())
                return Results.Json(ApiError.Create("not_found", "There is no order with that number.", "لا يوجد طلب بهذا الرقم."), statusCode: 404);
            await q.Query("sales.order_event").InsertAsync(new Dictionary<string, object?>
            {
                ["invoice_no"] = invoiceNo, ["kind"] = body.Kind,
                ["detail"] = body.Detail is { Length: > 200 } d ? d[..200] : body.Detail,
                ["actor"] = user.Username, ["at_utc"] = DateTime.UtcNow,
            });
            return Results.NoContent();
        });

        g.MapGet("/{invoiceNo:long}/timeline", async (long invoiceNo, IDb db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var q = db.Query(conn);
            var sale = (await q.Query("sales.sale").Where("invoice_no", invoiceNo).GetAsync(cancellationToken: ct))
                .Cast<IDictionary<string, object?>>().FirstOrDefault();
            if (sale is null)
                return Results.Json(ApiError.Create("not_found", "There is no order with that number.", "لا يوجد طلب بهذا الرقم."), statusCode: 404);

            var doc = CatalogEndpoints.Parse(sale["document_json"]) ?? new JsonObject();
            var steps = new List<Step>();
            void Add(string kind, object? at, string? detail = null, string? actor = null)
            {
                var when = At(at);
                if (when is not null) steps.Add(new Step(kind, when.Value, detail, actor));
            }

            Add("placed", sale["sold_at_utc"], Str(doc, "orderType"), (string?)sale["cashier_name"]);

            var ticket = (await q.Query("sales.kitchen_ticket").Where("invoice_no", invoiceNo).GetAsync(cancellationToken: ct))
                .Cast<IDictionary<string, object?>>().FirstOrDefault();
            if (ticket is not null)
            {
                var t = CatalogEndpoints.Parse(ticket["document_json"]) ?? new JsonObject();
                Add("kitchen_sent", Str(t, "createdAt") ?? (object?)ticket["created_at_utc"]);
                Add("kitchen_started", Str(t, "startedAt"));
                Add("kitchen_ready", Str(t, "readyAt"));
                Add("collected", Str(t, "collectedAt"));
                Add("kitchen_cancelled", Str(t, "cancelledAt"), Str(t, "cancelReason"));
            }

            var events = await q.Query("sales.order_event").Where("invoice_no", invoiceNo).OrderBy("at_utc").GetAsync(cancellationToken: ct);
            foreach (var e in events.Cast<IDictionary<string, object?>>())
                Add((string)e["kind"]!, e["at_utc"], (string?)e["detail"], (string?)e["actor"]);

            if ((string)sale["status"]! == "voided")
                Add(Str(doc, "replacedByInvoice") is not null ? "edited" : "cancelled", sale["voided_at_utc"],
                    Str(doc, "replacedByInvoice") ?? (string?)sale["void_reason"], (string?)sale["voided_by"]);

            var ordered = steps.OrderBy(s => s.At).ToList();
            return Results.Ok(new
            {
                invoiceNo = invoiceNo.ToString(CultureInfo.InvariantCulture),
                orderNo = doc["orderNo"] is JsonValue ov && ov.TryGetValue<int>(out var on) ? on : (int?)null,
                totalH = Convert.ToInt64(sale["total_h"]),
                status = (string)sale["status"]!,
                kitchenStatus = ticket is null ? null : (string?)ticket["status"],
                steps = ordered.Select((s, i) => new
                {
                    s.Kind, atUtc = s.At, s.Detail, s.Actor,
                    secondsFromPrevious = i == 0 ? (double?)null : Math.Round((s.At - ordered[i - 1].At).TotalSeconds),
                }),
                totalSeconds = ordered.Count > 1 ? Math.Round((ordered[^1].At - ordered[0].At).TotalSeconds) : 0,
            });
        });
    }

    private sealed record Step(string Kind, DateTime At, string? Detail, string? Actor);

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static DateTime? At(object? value) => value switch
    {
        null => null,
        DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        _ => null,
    };
}
