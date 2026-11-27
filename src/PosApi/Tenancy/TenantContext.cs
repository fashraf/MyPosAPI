namespace PosApi.Tenancy;

/// <summary>
/// SaaS mode. One API serves every client; each client has its own database, chosen per request from the address
/// (pos1.wateeq.com → client "pos1"). Off by default: a single-business server keeps working exactly as before.
/// </summary>
public sealed class TenancyOptions
{
    public const string Section = "Tenancy";
    /// <summary>"single" (default) or "multi".</summary>
    public string Mode { get; set; } = "single";
    /// <summary>Clients live at &lt;slug&gt;.&lt;RootDomain&gt;, e.g. wateeq.com.</summary>
    public string RootDomain { get; set; } = "wateeq.com";
    /// <summary>The super admin app (POSSA). Requests to this host reach only /api/sa and /health.</summary>
    public string AdminHost { get; set; } = "admin.wateeq.com";
    public bool IsMulti => string.Equals(Mode, "multi", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The client a request belongs to.</summary>
public sealed record TenantInfo(long Id, string Slug, string Name, string Status, string ConnectionString);

/// <summary>Flows with the request (and anything it awaits), so the database layer needs no extra parameter.</summary>
public static class TenantContext
{
    private static readonly AsyncLocal<TenantInfo?> CurrentTenant = new();
    private static readonly AsyncLocal<bool> AdminRequest = new();
    public static TenantInfo? Current { get => CurrentTenant.Value; set => CurrentTenant.Value = value; }
    /// <summary>A request to the super admin app (POSSA).</summary>
    public static bool Admin { get => AdminRequest.Value; set => AdminRequest.Value = value; }
    /// <summary>Name used in tokens and logs: the client slug, or "admin" / "single".</summary>
    public static string Key => Current?.Slug ?? (Admin ? "admin" : "single");
}
