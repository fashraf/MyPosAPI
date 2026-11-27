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
}
