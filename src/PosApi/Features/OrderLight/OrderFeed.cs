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
/// <summary>One feed per client (SaaS mode); a single business has one.</summary>
public sealed class OrderFeed
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Feed> _feeds = new();
    private Feed Current => _feeds.GetOrAdd(PosApi.Tenancy.TenantContext.Key, _ => new Feed());

    public string BootId { get; } = Guid.NewGuid().ToString("N")[..12];
    public long LastSeq => Current.LastSeq;
    public int TestId => Current.TestId;
    public void Test() => Current.Test();
    public Task EnsureSeededAsync(Func<Task<long>> latestInvoice) => Current.EnsureSeededAsync(latestInvoice);
    public void Publish(long invoiceNo, int orderNo, long totalH, string branchId, string? orderType) => Current.Publish(invoiceNo, orderNo, totalH, branchId, orderType);
    public (long LastSeq, IReadOnlyList<OrderEvent> Orders) Since(long since, int max = 50) => Current.Since(since, max);
}

internal sealed class Feed
{
    private const int Keep = 500;
    private readonly object _gate = new();
    private readonly LinkedList<OrderEvent> _events = new();
    private long _seq;

    public long LastSeq { get { lock (_gate) return _seq; } }

    /// <summary>Bumped by "Test the light" in the back office; boards blink blue-green when it changes.</summary>
    public int TestId { get; private set; }
    public void Test() { lock (_gate) TestId++; }

    private bool _seeded;
    /// <summary>
    /// The cursor is the invoice number, so a board keeps its place across API restarts and deploys. On the first
    /// poll after a start the feed reads the latest invoice number once (one query per process, never per poll).
    /// </summary>
    public async Task EnsureSeededAsync(Func<Task<long>> latestInvoice)
    {
        if (_seeded) return;
        var latest = await latestInvoice();
        lock (_gate) { if (!_seeded) { _seq = Math.Max(_seq, latest); _seeded = true; } }
    }

    public void Publish(long invoiceNo, int orderNo, long totalH, string branchId, string? orderType)
    {
        lock (_gate)
        {
            _seq = Math.Max(_seq, invoiceNo);
            _events.AddLast(new OrderEvent(invoiceNo, invoiceNo, orderNo, totalH, branchId, orderType ?? "", DateTime.UtcNow));
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
        app.MapGet(Path, async (HttpContext http, OrderFeed feed, IConfiguration config, PosApi.Data.IDb db, long? since, string? bootId, CancellationToken ct) =>
        {
            var key = config["OrderLight:Key"];
            if (string.IsNullOrWhiteSpace(key))
                return Results.Json(new { code = "not_configured", messageEn = "The order light is not set up on this server (OrderLight:Key)." }, statusCode: 503);

            var sent = http.Request.Headers["X-Device-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(key)))
                return Results.Json(new { code = "bad_key", messageEn = "Wrong or missing device key." }, statusCode: 401);

            var poll = Math.Clamp(config.GetValue("OrderLight:PollSeconds", 10), 2, 3600);
            await feed.EnsureSeededAsync(async () =>
            {
                await using var conn = await db.OpenAsync(ct);
                var max = await SqlKata.Execution.QueryExtensions.MaxAsync<long?>(db.Query(conn).Query("sales.sale"), "invoice_no", cancellationToken: ct);
                return max ?? 0;
            });
            var (last, _) = feed.Since(long.MaxValue);

            /* First call, or a position ahead of the server (database restored): take the current position without
               blinking. The cursor is the invoice number, so a restart or deploy no longer loses orders. */
            if (since is null || since > last)
                return Results.Ok(new { bootId = feed.BootId, testId = feed.TestId, lastSeq = last, pollSeconds = poll, newOrders = 0, orders = Array.Empty<object>() });

            var (lastSeq, orders) = feed.Since(since.Value);
            /* Invoices are 1, 2, 3 without gaps, so orders placed while the feed was restarting still count. */
            var count = (int)Math.Min(lastSeq - since.Value, 50);
            return Results.Ok(new
            {
                bootId = feed.BootId, testId = feed.TestId, lastSeq, pollSeconds = poll, newOrders = count,
                orders = orders.Select(o => new
                {
                    o.Seq, invoice = o.InvoiceNo, orderNo = o.OrderNo, totalSar = o.TotalH / 100m, o.BranchId, o.OrderType, o.AtUtc,
                }),
            });
        })
        .AllowAnonymous()
        .RequireRateLimiting("standard")
        .WithTags("Order light");

        /* Back office "Test the light": makes every board blink without placing an order. */
        app.MapPost(Path + "/test", (OrderFeed feed) => { feed.Test(); return Results.Ok(new { testId = feed.TestId }); })
            .RequireAuthorization().RequireRateLimiting("standard").WithTags("Order light");

        /* Same test with the device key, for a quick check from PowerShell/curl without signing in. */
        app.MapPost(Path + "/ping", (HttpContext http, OrderFeed feed, IConfiguration config) =>
        {
            var key = config["OrderLight:Key"];
            var sent = http.Request.Headers["X-Device-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(key)))
                return Results.Json(new { code = "bad_key", messageEn = "Wrong or missing device key." }, statusCode: 401);
            feed.Test();
            return Results.Ok(new { testId = feed.TestId });
        }).AllowAnonymous().RequireRateLimiting("standard").WithTags("Order light");
    }
}
