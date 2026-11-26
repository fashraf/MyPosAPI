using System.Data.Common;
using Dapper;
using PosApi.Data;
using SqlKata.Execution;

namespace PosApi.Features.Access;

/// <summary>A signed-in user's row, as the auth flows need it.</summary>
public sealed record AuthUser
{
    public string UserId { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string RoleId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateOnly? AccessExpiresOn { get; init; }
    public string? PasswordHash { get; init; }
    public string? PasswordSalt { get; init; }
}

public sealed record RegistrationInput(
    string CompanyNameAr, string CompanyNameEn, string? CrNumber, string? VatNumber, string? City,
    string BranchNameAr, string BranchNameEn, string OwnerNameAr, string OwnerNameEn, string OwnerEmail, string? OwnerPhone);

/// <summary>Why registration was refused; mapped to the same API errors as before.</summary>
public enum RegistrationError { None, NameRequired, EmailRequired, EmailTaken, MobileTaken, BadVat }

/// <summary>
/// Data access for sign-in, sessions and registration. Engine-neutral: portable SQL and SqlKata only.
/// </summary>
public interface IAuthStore
{
    Task<DbConnection> OpenAsync(CancellationToken ct);
    Task<AuthUser?> FindByIdentifierAsync(DbConnection c, string identifier, CancellationToken ct);
    Task<AuthUser?> FindByIdAsync(DbConnection c, string userId, CancellationToken ct);
    Task<IReadOnlyList<string>> PermissionsAsync(DbConnection c, string userId, CancellationToken ct);
    Task<IReadOnlyList<string>> BranchesAsync(DbConnection c, string userId, CancellationToken ct);
    Task<string?> ChallengeIdentifierAsync(DbConnection c, string challengeId, CancellationToken ct);
    Task SaveSessionAsync(DbConnection c, string userId, string tokenHash, DateTime expiresAtUtc, CancellationToken ct, bool remember = false);
    Task<bool> IsRememberedAsync(DbConnection c, string tokenHash, CancellationToken ct);
    Task<string?> ActiveRefreshTokenUserAsync(DbConnection c, string tokenHash, CancellationToken ct);
    Task RotateRefreshTokenAsync(DbConnection c, string oldHash, string newHash, string userId, DateTime expiresAtUtc, CancellationToken ct, bool remember = false);
    Task RevokeRefreshTokenAsync(DbConnection c, string tokenHash, CancellationToken ct);
    Task<(RegistrationError Error, string? UserId, string? BranchId)> RegisterBusinessAsync(RegistrationInput input, CancellationToken ct);
}

public sealed class AuthStore(IDb db, INumberSeries numbers) : IAuthStore
{
    public Task<DbConnection> OpenAsync(CancellationToken ct) => db.OpenAsync(ct);

    public Task<AuthUser?> FindByIdentifierAsync(DbConnection c, string identifier, CancellationToken ct) =>
        c.QuerySingleOrDefaultAsync<AuthUser>(new CommandDefinition(
            "SELECT CAST(user_id AS VARCHAR(20)) AS user_id, username, name_ar, name_en, role_id, status, access_expires_on, password_hash, password_salt FROM core.app_user WHERE email = @Identifier OR phone = @Identifier OR username = @Identifier",
            new { Identifier = identifier }, cancellationToken: ct));

    public Task<AuthUser?> FindByIdAsync(DbConnection c, string userId, CancellationToken ct) =>
        c.QuerySingleOrDefaultAsync<AuthUser>(new CommandDefinition(
            "SELECT CAST(user_id AS VARCHAR(20)) AS user_id, username, name_ar, name_en, role_id, status, access_expires_on, password_hash, password_salt FROM core.app_user WHERE user_id = @UserId", new { UserId = Id(userId) }, cancellationToken: ct));

    /// <summary>Role permissions plus explicit grants, minus explicit denials (a denial always wins).</summary>
    public async Task<IReadOnlyList<string>> PermissionsAsync(DbConnection c, string userId, CancellationToken ct) =>
        (await c.QueryAsync<string>(new CommandDefinition(
            """
            SELECT p.permission_key
            FROM core.permission p
            JOIN core.app_user u ON u.user_id = @UserId
            WHERE (EXISTS (SELECT 1 FROM core.role_permission rp WHERE rp.role_id = u.role_id AND rp.permission_key = p.permission_key)
                   OR EXISTS (SELECT 1 FROM core.user_permission_override o
                              WHERE o.user_id = u.user_id AND o.permission_key = p.permission_key AND o.effect = 'grant'))
              AND NOT EXISTS (SELECT 1 FROM core.user_permission_override d
                              WHERE d.user_id = u.user_id AND d.permission_key = p.permission_key AND d.effect = 'deny')
            """, new { UserId = Id(userId) }, cancellationToken: ct))).ToList();

    public async Task<IReadOnlyList<string>> BranchesAsync(DbConnection c, string userId, CancellationToken ct) =>
        (await c.QueryAsync<string>(new CommandDefinition(
            "SELECT CAST(branch_id AS VARCHAR(20)) FROM core.user_branch WHERE user_id = @UserId", new { UserId = Id(userId) }, cancellationToken: ct))).ToList();

    public Task<string?> ChallengeIdentifierAsync(DbConnection c, string challengeId, CancellationToken ct) =>
        c.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT identifier FROM core.otp_challenge WHERE challenge_id = @ChallengeId", new { ChallengeId = challengeId }, cancellationToken: ct));

    public Task<bool> IsRememberedAsync(DbConnection c, string tokenHash, CancellationToken ct) =>
        c.QuerySingleOrDefaultAsync<bool>(new CommandDefinition(
            "SELECT remember_me FROM core.refresh_token WHERE token_hash = @Hash", new { Hash = tokenHash }, cancellationToken: ct));

    public async Task SaveSessionAsync(DbConnection c, string userId, string tokenHash, DateTime expiresAtUtc, CancellationToken ct, bool remember = false)
    {
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO core.refresh_token (token_hash, user_id, issued_at_utc, expires_at_utc, remember_me) VALUES (@Hash, @UserId, @Now, @Expires, @Remember)",
            new { Hash = tokenHash, UserId = Id(userId), Now = DateTime.UtcNow, Expires = expiresAtUtc, Remember = remember }, tx, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE core.app_user SET last_login_at_utc = @Now WHERE user_id = @UserId",
            new { Now = DateTime.UtcNow, UserId = Id(userId) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public Task<string?> ActiveRefreshTokenUserAsync(DbConnection c, string tokenHash, CancellationToken ct) =>
        c.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT CAST(user_id AS VARCHAR(20)) FROM core.refresh_token WHERE token_hash = @Hash AND revoked_at_utc IS NULL AND expires_at_utc > @Now",
            new { Hash = tokenHash, Now = DateTime.UtcNow }, cancellationToken: ct));

    public async Task RotateRefreshTokenAsync(DbConnection c, string oldHash, string newHash, string userId, DateTime expiresAtUtc, CancellationToken ct, bool remember = false)
    {
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE core.refresh_token SET revoked_at_utc = @Now WHERE token_hash = @OldHash",
            new { Now = DateTime.UtcNow, OldHash = oldHash }, tx, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO core.refresh_token (token_hash, user_id, issued_at_utc, expires_at_utc, remember_me) VALUES (@Hash, @UserId, @Now, @Expires, @Remember)",
            new { Hash = newHash, UserId = Id(userId), Now = DateTime.UtcNow, Expires = expiresAtUtc, Remember = remember }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public Task RevokeRefreshTokenAsync(DbConnection c, string tokenHash, CancellationToken ct) =>
        c.ExecuteAsync(new CommandDefinition(
            "UPDATE core.refresh_token SET revoked_at_utc = @Now WHERE token_hash = @Hash",
            new { Now = DateTime.UtcNow, Hash = tokenHash }, cancellationToken: ct));

    /// <summary>
    /// Company (first time only) + branch + branch settings + owner + owner's branch, in one transaction.
    /// Same rules as the old core.usp_RegisterBusiness, now in C# so it runs on any engine.
    /// </summary>
    public async Task<(RegistrationError Error, string? UserId, string? BranchId)> RegisterBusinessAsync(RegistrationInput r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.CompanyNameAr) || string.IsNullOrWhiteSpace(r.CompanyNameEn)) return (RegistrationError.NameRequired, null, null);
        if (string.IsNullOrWhiteSpace(r.OwnerEmail)) return (RegistrationError.EmailRequired, null, null);
        var vat = string.IsNullOrWhiteSpace(r.VatNumber) ? null : r.VatNumber.Trim();
        if (vat is not null && !(vat.Length == 15 && vat.All(char.IsAsciiDigit) && vat[0] == '3' && vat[^1] == '3'))
            return (RegistrationError.BadVat, null, null);
        var cr = string.IsNullOrWhiteSpace(r.CrNumber) ? null : r.CrNumber.Trim();

        return await db.InTransactionAsync<(RegistrationError, string?, string?)>(async (c, tx) =>
        {
            var q = db.Query(c);
            if (await q.Query("core.app_user").Where("email", r.OwnerEmail).ExistsAsync(tx))
                return (RegistrationError.EmailTaken, null, null);
            if (r.OwnerPhone is not null && await q.Query("core.app_user").Where("phone", r.OwnerPhone).ExistsAsync(tx))
                return (RegistrationError.MobileTaken, null, null);

            var seq = await numbers.NextAsync(c, tx, "branch");
            var branchId = seq;                                        // integer keys: branch 1, 2 …
            var userId = await numbers.NextAsync(c, tx, "user");
            var now = DateTime.UtcNow;

            if (!await q.Query("core.company").ExistsAsync(tx))
                await q.Query("core.company").InsertAsync(new
                {
                    name_ar = r.CompanyNameAr, name_en = r.CompanyNameEn, cr_number = cr, vat_number = vat, city = r.City, country = "SA",
                    created_at_utc = now, updated_at_utc = now,
                }, tx);
            await q.Query("core.branch").InsertAsync(new
            {
                branch_id = branchId, code = $"BR-{seq:000}", name_ar = r.BranchNameAr, name_en = r.BranchNameEn, phone = r.OwnerPhone,
                is_active = true, created_at_utc = now, updated_at_utc = now,
            }, tx);
            await q.Query("core.branch_settings").InsertAsync(new { branch_id = branchId, updated_at_utc = now }, tx);
            await q.Query("core.app_user").InsertAsync(new
            {
                user_id = userId, username = r.OwnerEmail, name_ar = r.OwnerNameAr, name_en = r.OwnerNameEn, email = r.OwnerEmail,
                phone = r.OwnerPhone, role_id = "rol_owner", status = "active", is_external = false, base_salary_h = 0L,
                employee_no = (int)await numbers.NextAsync(c, tx, "employee"),
                created_at_utc = now, created_by = "registration", updated_at_utc = now,
            }, tx);
            await q.Query("core.user_branch").InsertAsync(new { user_id = userId, branch_id = branchId }, tx);
            return (RegistrationError.None, userId.ToString(), branchId.ToString());
        }, ct);
    }

    /// <summary>Ids are integers in the database and text ("1") in tokens and JSON.</summary>
    private static long Id(string? value) => long.TryParse(value, out var id) ? id : -1;
}
