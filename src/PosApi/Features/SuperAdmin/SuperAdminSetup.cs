using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PosApi.Auth;
using PosApi.Data.Providers;
using PosApi.Tenancy;
using SqlKata.Execution;

namespace PosApi.Features.SuperAdmin;

public static class SuperAdminSetup
{
    /// <summary>"--create-admin email name": a super admin with a printed one-time password (change it after signing in).</summary>
    public static async Task<int> CreateAdminAsync(IConfiguration config, string email, string name, CancellationToken ct = default)
    {
        var provider = DatabaseProviders.Resolve(config[$"{PosApi.Data.DatabaseOptions.Section}:Provider"]);
        var store = new ControlStore(provider, config, Options.Create(config.GetSection(TenancyOptions.Section).Get<TenancyOptions>() ?? new TenancyOptions()));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('/', 'x').Replace('+', 'y');
        var (hash, salt) = new PasswordHasher().Hash(password);
        await using var c = await store.OpenAsync(ct);
        var q = store.Query(c);
        email = email.Trim().ToLowerInvariant();
        if (await q.Query("control.admin_user").Where("email", email).ExistsAsync())
            await q.Query("control.admin_user").Where("email", email).UpdateAsync(new Dictionary<string, object?> { ["password_hash"] = hash, ["password_salt"] = salt });
        else
            await q.Query("control.admin_user").InsertAsync(new Dictionary<string, object?>
                { ["email"] = email, ["name"] = name, ["password_hash"] = hash, ["password_salt"] = salt, ["created_at_utc"] = DateTime.UtcNow });
        Console.WriteLine($"Super admin {email} ready. One-time password: {password}");
        return 0;
    }

    /// <summary>
    /// "--register-main slug name": list this environment's own database (ConnectionStrings:PosMain) as a client, so it
    /// shows in POSSA and keeps serving at the server's IP (Tenancy:FallbackSlug). Does nothing if the slug exists.
    /// </summary>
    public static async Task<int> RegisterMainAsync(IConfiguration config, string slug, string name, CancellationToken ct = default)
    {
        var provider = DatabaseProviders.Resolve(config[$"{PosApi.Data.DatabaseOptions.Section}:Provider"]);
        var store = new ControlStore(provider, config, Options.Create(config.GetSection(TenancyOptions.Section).Get<TenancyOptions>() ?? new TenancyOptions()));
        var cs = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = config.GetConnectionString("PosMain") ?? "" };
        string Get(params string[] keys) => keys.Select(k => cs.TryGetValue(k, out var v) ? v?.ToString() : null).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
        await using var c = await store.OpenAsync(ct);
        var q = store.Query(c);
        slug = slug.Trim().ToLowerInvariant();
        if (await q.Query("control.tenant").Where("slug", slug).ExistsAsync(cancellationToken: ct)) { Console.WriteLine($"{slug}: already listed."); return 0; }
        var now = DateTime.UtcNow;
        var id = await q.Query("control.tenant").InsertGetIdAsync<long>(new Dictionary<string, object?>
        {
            ["slug"] = slug, ["name"] = name, ["status"] = "active", ["plan"] = "Pro", ["plan_code"] = "Pro", ["billing_cycle"] = "monthly",
            ["subscription_starts"] = now, ["subscription_ends"] = now.AddYears(1), ["city"] = "Riyadh", ["business_type"] = "Restaurant",
            ["db_host"] = Get("Host", "Server") is { Length: > 0 } h ? h : "localhost", ["db_port"] = int.TryParse(Get("Port"), out var p) ? p : 5432,
            ["db_name"] = Get("Database"), ["db_user"] = Get("Username", "User ID", "User"), ["db_password_enc"] = store.Protect(Get("Password")),
            ["registered_by"] = "setup", ["activated_by"] = "setup", ["activated_at_utc"] = now, ["created_at_utc"] = now, ["updated_at_utc"] = now,
        }, cancellationToken: ct);
        await PosApi.Data.Migrations.SchemaMigrator.MarkMigrated(store, id);
        await store.LogAsync(id, "created", $"{slug}: this environment's own database ({Get("Database")})", "setup", ct);
        Console.WriteLine($"{slug}: listed as client {id} ({Get("Database")}).");
        return 0;
    }
}
