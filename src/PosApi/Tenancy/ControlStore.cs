using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Extensions.Options;
using PosApi.Data.Providers;
using SqlKata.Execution;

namespace PosApi.Tenancy;

public sealed record TenantDb(string Host, int Port, string Database, string User, string Password);

/// <summary>The control database: which clients exist and where their databases are.</summary>
public sealed class ControlStore(IDatabaseProvider provider, IConfiguration config, IOptions<TenancyOptions> options)
{
    public const string SecretPurpose = "tenant-db";
    private readonly ConcurrentDictionary<string, (TenantInfo? Tenant, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public TenancyOptions Options => options.Value;

    public string ControlConnectionString => config.GetConnectionString("Control")
        ?? throw new InvalidOperationException("Connection string 'Control' is missing (ConnectionStrings__Control).");

    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var c = provider.CreateConnection(ControlConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    public QueryFactory Query(DbConnection c) => new(c, provider.Compiler);

    public IConfiguration Configuration => config;

    /// <summary>An unopened connection to any database of this engine (testing a client's credentials).</summary>
    public DbConnection CreateConnection(string connectionString) => provider.CreateConnection(connectionString);

    /// <summary>A connection string for a client database, in this engine's format.</summary>
    public string BuildConnectionString(TenantDb db) => provider.Name == "sqlserver"
        ? $"Server={db.Host},{db.Port};Database={db.Database};User Id={db.User};Password={db.Password};TrustServerCertificate=True"
        : $"Host={db.Host};Port={db.Port};Database={db.Database};Username={db.User};Password={db.Password};Timeout=10";

    public string Protect(string password) => SecretBox.Protect(password, config, SecretPurpose);

    /// <summary>pos1.wateeq.com → client "pos1" (or a client's own domain). Cached for 30 seconds.</summary>
    public async Task<TenantInfo?> ResolveAsync(string host, CancellationToken ct)
    {
        host = host.ToLowerInvariant();
        if (_cache.TryGetValue(host, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Tenant;

        var root = "." + Options.RootDomain.ToLowerInvariant();
        var slug = host.EndsWith(root, StringComparison.Ordinal) ? host[..^root.Length] : null;
        await using var c = await OpenAsync(ct);
        var q = Query(c).Query("control.tenant");
        q = slug is not null && !slug.Contains('.') ? q.Where("slug", slug) : q.Where("custom_host", host);
        var row = (await q.GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();
        var tenant = row is null ? null : ToTenant(row);
        _cache[host] = (tenant, DateTime.UtcNow);
        return tenant;
    }

    public void Forget() => _cache.Clear();

    public TenantInfo ToTenant(IDictionary<string, object?> r) => new(
        Convert.ToInt64(r["tenant_id"]), (string)r["slug"]!, (string)r["name"]!, (string)r["status"]!,
        BuildConnectionString(new TenantDb((string)r["db_host"]!, Convert.ToInt32(r["db_port"]), (string)r["db_name"]!, (string)r["db_user"]!,
            SecretBox.Unprotect((string)r["db_password_enc"]!, config, SecretPurpose))));

    public async Task<List<TenantInfo>> AllAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        return (await Query(c).Query("control.tenant").OrderBy("tenant_id").GetAsync(cancellationToken: ct))
            .Cast<IDictionary<string, object?>>().Select(ToTenant).ToList();
    }

    public async Task LogAsync(long? tenantId, string kind, string? detail, string? actor, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await Query(c).Query("control.tenant_event").InsertAsync(new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["kind"] = kind, ["detail"] = detail is { Length: > 4000 } ? detail[..4000] : detail,
            ["actor"] = actor, ["at_utc"] = DateTime.UtcNow,
        }, cancellationToken: ct);
    }
}
