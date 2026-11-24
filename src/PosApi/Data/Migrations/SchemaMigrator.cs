using FluentMigrator.Runner;
using PosApi.Data.Providers;

namespace PosApi.Data.Migrations;

/// <summary>
/// "dotnet PosApi.dll --migrate": brings the configured database up to date and exits (0 = ok).
/// Migrations are C# classes in this folder, written once and generated for whichever engine is configured.
/// Rules: never edit a migration that has run; add a new one. Add first, drop/rename only in a later release.
/// </summary>
public static class SchemaMigrator
{
    public static int Run(IConfiguration configuration)
    {
        var provider = DatabaseProviders.Resolve(configuration[$"{DatabaseOptions.Section}:Provider"]);
        var cs = configuration.GetConnectionString("PosMain")
            ?? throw new InvalidOperationException("Connection string 'PosMain' is missing.");
        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                provider.AddMigrationEngine(rb);
                rb.WithGlobalConnectionString(cs).ScanIn(typeof(SchemaMigrator).Assembly).For.Migrations();
            })
            .AddLogging(lb => lb.AddFluentMigratorConsole())
            .BuildServiceProvider(false);
        try
        {
            using var scope = services.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
            Console.WriteLine($"Database is up to date ({provider.Name}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Migration failed ({provider.Name}): {ex.Message}");
            return 1;
        }
    }
}
