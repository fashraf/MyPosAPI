using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PosApi.Tenancy;
using SqlKata.Execution;

namespace PosApi.Observability;

public sealed record ErrorReport(string Source, string App, string Message, string? Detail = null, string? Url = null, string? Method = null,
    int? StatusCode = null, string? User = null, string? UserAgent = null, string? Ip = null, long? TenantId = null);

public sealed record UiEventReport(string App, string Kind, string? Page, string? Label, int? DurationMs, string? Actor, long? TenantId);

/// <summary>
/// Every error from every client and from Ayn lands in control.error_log; the same error repeated is one row with a
/// count (grouped by a fingerprint of where it came from and what it said, numbers ignored). Written in the
/// background so reporting never slows or breaks a request. SaaS mode only.
/// </summary>
public sealed partial class ErrorLog(IServiceProvider services, IOptions<TenancyOptions> options, ILogger<ErrorLog> logger) : BackgroundService
{
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Report(ErrorReport e) { if (options.Value.IsMulti) _queue.Writer.TryWrite(e); }
    public void Track(UiEventReport e) { if (options.Value.IsMulti) _queue.Writer.TryWrite(e); }

    public static string SourceOf(Exception? ex) => ex switch
    {
        DbException => "sql",
        HttpRequestException or TimeoutException or OperationCanceledException => "network",
        _ when ex?.GetType().Name.Contains("Postgres", StringComparison.Ordinal) == true => "sql",
        _ => "backend",
    };

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stop))
        {
            try
            {
                var control = services.GetRequiredService<ControlStore>();
                await using var c = await control.OpenAsync(stop);
                var q = control.Query(c);
                var now = DateTime.UtcNow;
                if (item is UiEventReport u)
                {
                    await q.Query("control.ui_event").InsertAsync(new Dictionary<string, object?>
                    {
                        ["tenant_id"] = u.TenantId, ["app"] = Cut(u.App, 20), ["actor"] = Cut(u.Actor, 160), ["kind"] = Cut(u.Kind, 20),
                        ["page"] = Cut(u.Page, 200), ["label"] = Cut(u.Label, 300), ["duration_ms"] = u.DurationMs, ["at_utc"] = now,
                    }, cancellationToken: stop);
                    continue;
                }
                var e = (ErrorReport)item;
                var fp = Fingerprint($"{e.Source}|{e.App}|{e.TenantId}|{Digits().Replace(e.Message, "#")}|{Digits().Replace(e.Url ?? "", "#")}");
                var existing = (await q.Query("control.error_log").Where("fingerprint", fp).Where("resolved", false)
                    .Select("error_id").Limit(1).GetAsync(cancellationToken: stop)).Cast<IDictionary<string, object?>>().FirstOrDefault();
                if (existing is not null)
                    await c.ExecuteUpdate(control, Convert.ToInt64(existing["error_id"]), now, e, stop);
                else
                    await q.Query("control.error_log").InsertAsync(new Dictionary<string, object?>
                    {
                        ["tenant_id"] = e.TenantId, ["source"] = Cut(e.Source, 20), ["app"] = Cut(e.App, 20), ["message"] = Cut(e.Message, 600) ?? "(no message)",
                        ["detail"] = Cut(e.Detail, 20000), ["url"] = Cut(e.Url, 400), ["method"] = Cut(e.Method, 10), ["status_code"] = e.StatusCode,
                        ["user_name"] = Cut(e.User, 160), ["user_agent"] = Cut(e.UserAgent, 300), ["ip"] = Cut(e.Ip, 60), ["fingerprint"] = fp,
                        ["occurrences"] = 1, ["first_at_utc"] = now, ["last_at_utc"] = now, ["resolved"] = false,
                    }, cancellationToken: stop);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not record an error or front-end event.");
            }
        }
    }

    private static string Fingerprint(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32].ToLowerInvariant();
    internal static string? Cut(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n];

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}

internal static class ErrorLogSql
{
    public static async Task ExecuteUpdate(this DbConnection c, ControlStore control, long id, DateTime now, ErrorReport e, CancellationToken ct)
    {
        var q = control.Query(c);
        var row = (await q.Query("control.error_log").Where("error_id", id).Select("occurrences").GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().First();
        await q.Query("control.error_log").Where("error_id", id).UpdateAsync(new Dictionary<string, object?>
        {
            ["occurrences"] = Convert.ToInt32(row["occurrences"]) + 1, ["last_at_utc"] = now,
            ["detail"] = ErrorLog.Cut(e.Detail, 20000), ["user_name"] = ErrorLog.Cut(e.User, 160), ["status_code"] = e.StatusCode,
        }, cancellationToken: ct);
    }
}
