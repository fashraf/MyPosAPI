using System.Security.Claims;

namespace PosApi.Auth;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string UserId { get; }
    string Username { get; }
    string RoleId { get; }
    IReadOnlySet<string> Permissions { get; }
    IReadOnlySet<string> BranchIds { get; }
    bool Can(string permission);
    bool CanSeeBranch(string? branchId);
}

public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    /* Read the principal when asked, not when constructed. The audit middleware resolves this service before
       authentication has run; a principal captured then stays anonymous for the whole request (that made /me
       return 401 for every signed-in user). */
    private ClaimsPrincipal? _principal => _accessor.HttpContext?.User;

    public IReadOnlySet<string> Permissions =>
        _principal?.FindAll(JwtTokenService.PermissionClaim)
            .Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? new HashSet<string>();

    public IReadOnlySet<string> BranchIds =>
        _principal?.FindAll(JwtTokenService.BranchClaim)
            .Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? new HashSet<string>();

    public bool IsAuthenticated => _principal?.Identity?.IsAuthenticated ?? false;

    /* Inbound claim mapping is off, so these are the names the token actually
       carries. The ClaimTypes fallbacks cover a token minted before that
       change, so an existing session does not break on deploy. */
    public string UserId =>
        _principal?.FindFirstValue("sub")
        ?? _principal?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? string.Empty;

    public string Username =>
        _principal?.FindFirstValue("unique_name")
        ?? _principal?.FindFirstValue(ClaimTypes.Name)
        ?? string.Empty;

    public string RoleId =>
        _principal?.FindFirstValue(ClaimTypes.Role)
        ?? _principal?.FindFirstValue("role")
        ?? string.Empty;


    public bool Can(string permission) => Permissions.Contains(permission);

    /// <summary>
    /// A null branch means the record is not branch-specific and is visible
    /// everywhere — the same rule the frontend applies, kept identical so a
    /// record cannot be visible in one layer and hidden in the other.
    /// </summary>
    public bool CanSeeBranch(string? branchId)
        => branchId is null || BranchIds.Count == 0 || BranchIds.Contains(branchId);
}
