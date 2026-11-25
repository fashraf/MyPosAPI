using System.Security.Cryptography;
using System.Text;

namespace PosApi.Features.OrderLight;

/// <summary>One order, as the order light sees it.</summary>
public sealed record OrderEvent(long Seq, long InvoiceNo, int OrderNo, long TotalH, string BranchId, string OrderType, DateTime AtUtc);

/// <summary>
/// New orders, kept in memory: the sale transaction announces each order here after it commits, and the order light
/// (an ESP32 board) polls this feed. Polling never touches the database, so a board checking every few seconds costs
/// nothing there.
///
/// The feed lives in the API process: after a restart it starts empty with a new BootId, and the board simply takes
/// the new position without blinking. (Several API instances behind a load balancer would need a shared feed, e.g.
/// Redis; this deployment runs one.)
/// </summary>
public sealed class OrderFeed
{
    private const int Keep = 500;
    private readonly object _gate = new();
    private readonly LinkedList<OrderEvent> _events = new();
    private long _seq;

    /// <summary>Changes every time the API starts, so a board knows its old position no longer applies.</summary>
    public string BootId { get; } = Guid.NewGuid().ToString("N")[..12];

    public long LastSeq { get { lock (_gate) return _seq; } }

    public void Publish(long invoiceNo, int orderNo, long totalH, string branchId, string? orderType)
    {
        lock (_gate)
        {
            _events.AddLast(new OrderEvent(++_seq, invoiceNo, orderNo, totalH, branchId, orderType ?? "", DateTime.UtcNow));
            while (_events.Count > Keep) _events.RemoveFirst();
        }
    }

    /// <summary>Orders after <paramref name="since"/>, oldest first (at most <paramref name="max"/>).</summary>
    public (long LastSeq, IReadOnlyList<OrderEvent> Orders) Since(long since, int max = 50)
    {
        lock (_gate)
        {
            var list = _events.Where(e => e.Seq > since).Take(max).ToList();
            return (_seq, list);
        }
    }
}

public static class OrderLightEndpoints
{
    public const string Path = "/api/device/order-feed";

    public static void MapOrderLightEndpoints(this IEndpointRouteBuilder app)
    {
        /* No user sign-in: a board has no person behind it. It sends the device key in X-Device-Key instead. */
        app.MapGet(Path, (HttpContext http, OrderFeed feed, IConfiguration config, long? since, string? bootId) =>
        {
            var key = config["OrderLight:Key"];
            if (string.IsNullOrWhiteSpace(key))
                return Results.Json(new { code = "not_configured", messageEn = "The order light is not set up on this server (OrderLight:Key)." }, statusCode: 503);

            var sent = http.Request.Headers["X-Device-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(key)))
                return Results.Json(new { code = "bad_key", messageEn = "Wrong or missing device key." }, statusCode: 401);

            var poll = Math.Clamp(config.GetValue("OrderLight:PollSeconds", 10), 2, 3600);
            var (last, _) = feed.Since(long.MaxValue);

            /* First call, a server restart (new bootId) or a position ahead of the server: take the current position
               without reporting old orders, so the board does not blink for orders it never missed. */
            if (since is null || bootId != feed.BootId || since > last)
                return Results.Ok(new { bootId = feed.BootId, lastSeq = last, pollSeconds = poll, newOrders = 0, orders = Array.Empty<object>() });

            var (lastSeq, orders) = feed.Since(since.Value);
            return Results.Ok(new
            {
                bootId = feed.BootId, lastSeq, pollSeconds = poll, newOrders = orders.Count,
                orders = orders.Select(o => new
                {
                    o.Seq, invoice = o.InvoiceNo, orderNo = o.OrderNo, totalSar = o.TotalH / 100m, o.BranchId, o.OrderType, o.AtUtc,
                }),
            });
        })
        .AllowAnonymous()
        .RequireRateLimiting("standard")
        .WithTags("Order light");
    }
}
