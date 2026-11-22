using Dapper;
using Microsoft.Extensions.Options;
using Nazad.Shared;
using PosApi.Data;

namespace PosApi.Auth;

public sealed class LicensingOptions
{
    public const string SectionName = "Licensing";

    /// <summary>
    /// Whether sign-in checks the POSSA licence.
    ///
    /// Off by default, so a Nazad installation without POSSA deployed keeps
    /// working. Turned on, it is the thing that makes the countdown mean
    /// something — without it, an expired licence is a number on a dashboard.
    /// </summary>
    public bool Enforce { get; init; }

    /// <summary>The control database. Named, not a connection string.</summary>
    public string ControlDatabase { get; init; } = "POSSA_CONTROL";
}

/// <summary>
/// Asks POSSA whether this business, and this person, may sign in.
///
/// Called just before a session is issued, in both login flows. A check anywhere
/// later — a middleware, a page — would let the token be minted first, and a
/// token that exists can be used until it expires.
/// </summary>
public sealed class LicenseGuard
{
    private readonly LicensingOptions _options;
    private readonly ILogger<LicenseGuard> _logger;

    public LicenseGuard(IOptions<LicensingOptions> options, ILogger<LicenseGuard> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(bool Allowed, string? Reason, int? DaysLeft)> CheckAsync(
        System.Data.Common.DbConnection connection, string userId, CancellationToken ct)
    {
        if (!_options.Enforce) return (true, null, null);

        /* The control database name reaches a three-part name, so it is held to
           the same rule as a tenant database name. */
        if (!DatabaseName.IsValid(_options.ControlDatabase))
        {
            _logger.LogError("Licensing:ControlDatabase is not a valid database name.");
            return (false, "Licensing is misconfigured. Contact support.", null);
        }

        var parameters = new DynamicParameters();
        parameters.Add("@DatabaseName", connection.Database);
        parameters.Add("@UserId", userId);
        parameters.Add("@Allowed", dbType: System.Data.DbType.Boolean,
                       direction: System.Data.ParameterDirection.Output);
        parameters.Add("@Reason", dbType: System.Data.DbType.String,
                       direction: System.Data.ParameterDirection.Output, size: 400);
        parameters.Add("@DaysLeft", dbType: System.Data.DbType.Int32,
                       direction: System.Data.ParameterDirection.Output);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"{_options.ControlDatabase}.dbo.usp_CheckAccess", parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (Exception ex)
        {
            /*
             * Refuse when the check cannot run.
             *
             * This is a licensing control, and failing open would mean an
             * unreachable control database silently unlicenses every tenant.
             * The cost is real — a control-database outage stops sign-ins — and
             * it is logged loudly so it is noticed and fixed rather than lived
             * with.
             */
            _logger.LogError(ex, "Licence check failed; refusing sign-in.");
            return (false, "Your licence could not be verified. Try again shortly.", null);
        }

        return (parameters.Get<bool>("@Allowed"),
                parameters.Get<string?>("@Reason"),
                parameters.Get<int?>("@DaysLeft"));
    }
}
