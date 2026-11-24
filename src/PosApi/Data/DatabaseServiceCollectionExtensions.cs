using PosApi.Data.Providers;

namespace PosApi.Data;

public static class DatabaseServiceCollectionExtensions
{
    /// <summary>Registers the configured engine, IDb and the number series. One call in Program.cs.</summary>
    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.Section));
        services.AddSingleton(DatabaseProviders.Resolve(configuration[$"{DatabaseOptions.Section}:Provider"]));
        services.AddSingleton<IDb, Db>();
        services.AddSingleton<INumberSeries, NumberSeries>();
        return services;
    }
}
