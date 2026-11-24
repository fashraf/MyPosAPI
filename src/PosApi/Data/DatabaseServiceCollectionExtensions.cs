using PosApi.Data.Providers;

namespace PosApi.Data;

public static class DatabaseServiceCollectionExtensions
{
    /// <summary>Registers the configured engine, IDb and the number series. One call in Program.cs.</summary>
    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        // snake_case columns (user_id) map to C# properties (UserId) without per-query aliases.
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.Section));
        services.AddSingleton(DatabaseProviders.Resolve(configuration[$"{DatabaseOptions.Section}:Provider"]));
        services.AddSingleton<IDb, Db>();
        services.AddSingleton<INumberSeries, NumberSeries>();
        services.AddSingleton<PosApi.Features.Access.IAuthStore, PosApi.Features.Access.AuthStore>();
        return services;
    }
}
