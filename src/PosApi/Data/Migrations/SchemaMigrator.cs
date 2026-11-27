using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using PosApi.Data.Providers;

namespace PosApi.Data.Migrations;

/// <summary>
/// "dotnet PosApi.dll --migrate": brings the database(s) up to date and exits (0 = ok).
/// Single mode: the PosMain database. SaaS mode (Tenancy:Mode=multi): the control database, then every client's.
/// Migrations are C# classes, written once and generated for whichever engine is configured.
/// Rules: never edit a migration that has run; add a new one. Add first, drop/rename only in a later release.
/// </summary>
public static class SchemaMigrator
{
    public const string ClientNamespace = "PosApi.Data.Migrations";
    public const string ControlNamespace = "PosApi.Data.ControlMigrations";

    public static int Run(IConfiguration configuration)
    {
        var multi = string.Equals(configuration["Tenancy:Mode"], "multi", StringComparison.OrdinalIgnoreCase);
        if (!multi)
        {
            var cs = configuration.GetConnectionString("PosMain") ?? throw new InvalidOperationException("Connection string 'PosMain' is missing.");
            return Migrate(configuration, cs, ClientNamespace, "database") ? 0 : 1;
        }

        var control = configuration.GetConnectionString("Control") ?? throw new InvalidOperationException("Connection string 'Control' is missing.");
        if (!Migrate(configuration, control, ControlNamespace, "control database")) return 1;
        return MigrateAllClients(configuration).GetAwaiter().GetResult();
    }

    /// <summary>Every client database; one failing does not stop the others. Exit code 1 if any failed.</summary>
    public static async Task<int> MigrateAllClients(IConfiguration configuration, CancellationToken ct = default)
    {
        var provider = DatabaseProviders.Resolve(configuration[$"{DatabaseOptions.Section}:Provider"]);
        var store = new Tenancy.ControlStore(provider, configuration, Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(Tenancy.TenancyOptions.Section).Get<Tenancy.TenancyOptions>() ?? new Tenancy.TenancyOptions()));
        var failed = 0;
        foreach (var tenant in await store.AllAsync(ct))
        {
            var ok = Migrate(configuration, tenant.ConnectionString, ClientNamespace, $"client {tenant.Slug}");
            if (!ok) failed++;
            await store.LogAsync(tenant.Id, ok ? "migrated" : "migration_failed", ok ? "Database up to date." : "See the deploy log.", "deploy", ct);
            if (ok) await MarkMigrated(store, tenant.Id);
        }
        Console.WriteLine(failed == 0 ? "All client databases are up to date." : $"{failed} client database(s) failed to migrate.");
        return failed == 0 ? 0 : 1;
    }

    public static bool Migrate(IConfiguration configuration, string connectionString, string ns, string label)
    {
        var provider = DatabaseProviders.Resolve(configuration[$"{DatabaseOptions.Section}:Provider"]);
        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                provider.AddMigrationEngine(rb);
                rb.WithGlobalConnectionString(connectionString).ScanIn(typeof(SchemaMigrator).Assembly).For.Migrations();
            })
            /* Control and client migrations live in different namespaces and never mix. */
            .Configure<TypeFilterOptions>(o => { o.Namespace = ns; o.NestedNamespaces = false; })
            .AddLogging(lb => lb.AddFluentMigratorConsole())
            .BuildServiceProvider(false);
        try
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            Console.WriteLine($"{label}: up to date ({provider.Name}).");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{label}: migration failed ({provider.Name}): {ex.Message}");
            return false;
        }
    }

    public static long LatestClientVersion() => typeof(SchemaMigrator).Assembly.GetTypes()
        .Where(t => t.Namespace == ClientNamespace)
        .Select(t => t.GetCustomAttributes(typeof(FluentMigrator.MigrationAttribute), false).OfType<FluentMigrator.MigrationAttribute>().FirstOrDefault()?.Version ?? 0)
        .DefaultIfEmpty(0).Max();

    public static async Task MarkMigrated(Tenancy.ControlStore store, long tenantId)
    {
        await using var c = await store.OpenAsync(CancellationToken.None);
        await SqlKata.Execution.QueryExtensions.UpdateAsync(store.Query(c).Query("control.tenant").Where("tenant_id", tenantId),
            new Dictionary<string, object?> { ["schema_version"] = LatestClientVersion(), ["migrated_at_utc"] = DateTime.UtcNow });
    }
}
