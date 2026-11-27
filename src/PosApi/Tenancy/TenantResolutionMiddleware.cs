using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PosApi.Tenancy;

/// <summary>
/// SaaS mode: before anything touches a database, work out which client this request is for from its address and
/// point the database layer at that client's database. Unknown address → 404; suspended client → 403.
/// The super admin host (admin.wateeq.com) only reaches /api/sa and /health.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, IOptions<TenancyOptions> options)
{
    public async Task InvokeAsync(HttpContext http, ControlStore control)
    {
        if (!options.Value.IsMulti) { await next(http); return; }

        var path = http.Request.Path;
        var host = http.Request.Host.Host;
        if (path.StartsWithSegments("/health") && !path.StartsWithSegments("/health/db")) { await next(http); return; }

        /* Ayn itself: its own address, its API, or its error reports (sent with header X-App: ayn from the IP address too). */
        if (string.Equals(host, options.Value.AdminHost, StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/api/sa")
            || (path.StartsWithSegments("/api/telemetry") && http.Request.Headers["X-App"] == "ayn"))
        {
            if (!path.StartsWithSegments("/api/sa") && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/api/telemetry"))
            {
                await Refuse(http, 404, "not_found", "Not found.", "غير موجود.");
                return;
            }
            TenantContext.Current = null;
            TenantContext.Admin = true;
            http.Items["tenant"] = "admin";
            try { await next(http); }
            finally { TenantContext.Admin = false; }
            return;
        }

        var tenant = await control.ResolveAsync(host, http.RequestAborted);
        if (tenant is null && !string.IsNullOrWhiteSpace(options.Value.FallbackSlug))
            tenant = await control.ResolveAsync($"{options.Value.FallbackSlug}.{options.Value.RootDomain}", http.RequestAborted);
        if (tenant is null) { await Refuse(http, 404, "unknown_client", $"No business is set up at {host}.", "لا توجد منشأة على هذا العنوان."); return; }
        if (tenant.Status == "suspended") { await Refuse(http, 403, "client_suspended", "This account is paused. Contact support.", "هذا الحساب موقوف مؤقتًا. تواصل مع الدعم."); return; }

        TenantContext.Current = tenant;
        http.Items["tenant"] = tenant.Slug;
        try { await next(http); }
        finally { TenantContext.Current = null; }
    }

    private static async Task Refuse(HttpContext http, int status, string code, string en, string ar)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(JsonSerializer.Serialize(new { code, messageEn = en, messageAr = ar }));
    }
}

/// <summary>A token issued for one client never works for another (all clients share the signing key).</summary>
public sealed class TenantTokenGuardMiddleware(RequestDelegate next, IOptions<TenancyOptions> options)
{
    public const string Claim = "tnt";

    public async Task InvokeAsync(HttpContext http)
    {
        if (options.Value.IsMulti && http.User.Identity?.IsAuthenticated == true)
        {
            var expected = http.Items["tenant"] as string ?? "single";
            var actual = http.User.FindFirst(Claim)?.Value;
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                http.Response.StatusCode = 401;
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync("{\"code\":\"wrong_client\",\"messageEn\":\"Sign in again.\",\"messageAr\":\"سجّل الدخول مرة أخرى.\"}");
                return;
            }
        }
        await next(http);
    }
}
