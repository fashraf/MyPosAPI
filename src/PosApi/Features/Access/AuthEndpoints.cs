using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PosApi.Auth;
using PosApi.Common;
using Microsoft.Extensions.Options;
using PosApi.Data;

namespace PosApi.Features.Access;

public sealed record LoginRequest(string Identifier, string? Password, bool? RememberMe = null);

public sealed record RegistrationOwner(
    string FirstName,
    string LastName,
    string Email,
    string Mobile,
    string? Password);

public sealed record RegistrationBusiness(
    string NameEn,
    string NameAr,
    string? BusinessType,
    string? BusinessCategory,
    string? Country,
    string? City,
    string? District,
    string? Address);

public sealed record RegistrationTax(
    string? CrNumber,
    string? VatNumber,
    string? TaxStartDate,
    bool IsVatRegistered);

public sealed record RegisterRequest(
    RegistrationOwner Owner,
    RegistrationBusiness Business,
    RegistrationTax Tax);
public sealed record OtpRequest(string ChallengeId, string Code, bool? RememberMe = null);
public sealed record RefreshRequest(string RefreshToken);

public sealed record SessionResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAtUtc,
    UserSummary User);

public sealed record UserSummary(
    string UserId,
    string Username,
    string NameAr,
    string NameEn,
    string RoleId,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> BranchIds);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        /* The strict limit is for what an attacker would hammer: creating accounts, asking for codes, guessing codes.
           Staying signed in (who am I, refresh, sign out) runs on every page load of every till, so it uses the
           normal per-user limit; under the strict one a reload burst signed people out (429 on /me). */
        group.MapPost("/register", RegisterAsync).RequireRateLimiting("auth");
        group.MapPost("/login", RequestOtpAsync).RequireRateLimiting("auth");
        group.MapPost("/verify", VerifyOtpAsync).RequireRateLimiting("auth");
        group.MapPost("/refresh", RefreshAsync).RequireRateLimiting("standard");
        group.MapPost("/logout", LogoutAsync).RequireAuthorization().RequireRateLimiting("standard");
        group.MapGet("/me", MeAsync).RequireAuthorization().RequireRateLimiting("standard");
    }

    /// <summary>
    /// Create a business and its owner.
    ///
    /// One stored procedure call: the company, the branch, its settings and the
    /// owner account are created together. Half of that succeeding leaves an
    /// account nobody can sign in to and nobody can remove from the UI.
    ///
    /// No session is issued here. Registration ends at the OTP step, so an
    /// address nobody controls cannot become a working account.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        IAuthStore store,
        IOtpService otp,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var fieldErrors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Business.NameAr))
            fieldErrors["business.nameAr"] = ["The Arabic business name is required."];

        if (string.IsNullOrWhiteSpace(request.Business.NameEn))
            fieldErrors["business.nameEn"] = ["The English business name is required."];

        if (string.IsNullOrWhiteSpace(request.Owner.Email))
            fieldErrors["owner.email"] = ["An email address is required."];

        if (fieldErrors.Count > 0)
        {
            return Results.BadRequest(ApiError.Create(
                "validation_failed",
                "Some details are missing.",
                "بعض البيانات ناقصة.",
                fieldErrors));
        }

        var ownerName = $"{request.Owner.FirstName} {request.Owner.LastName}".Trim();

        var (error, userId, branchId) = await store.RegisterBusinessAsync(new RegistrationInput(
            request.Business.NameAr, request.Business.NameEn,
            request.Tax.IsVatRegistered ? request.Tax.CrNumber : null,
            request.Tax.IsVatRegistered ? request.Tax.VatNumber : null,
            request.Business.City, request.Business.NameAr, request.Business.NameEn,
            // The wizard collects one name. Storing it in both columns keeps the NOT NULL contract honest.
            ownerName, ownerName, request.Owner.Email.Trim(),
            string.IsNullOrWhiteSpace(request.Owner.Mobile) ? null : request.Owner.Mobile.Trim()), ct);

        if (error != RegistrationError.None)
        {
            var apiError = error switch
            {
                RegistrationError.NameRequired => ApiError.Create("name_required",
                    "The business needs both an Arabic and an English name.",
                    "المنشأة تحتاج اسمًا بالعربية وآخر بالإنجليزية."),
                RegistrationError.EmailRequired => ApiError.Create("email_required",
                    "An email address is required.", "البريد الإلكتروني مطلوب."),
                RegistrationError.EmailTaken => ApiError.Create("email_taken",
                    "An account already exists for that email address.",
                    "يوجد حساب مسجّل بهذا البريد الإلكتروني."),
                RegistrationError.MobileTaken => ApiError.Create("mobile_taken",
                    "An account already exists for that mobile number.",
                    "يوجد حساب مسجّل بهذا الرقم."),
                RegistrationError.BadVat => ApiError.Create("bad_vat",
                    "A Saudi VAT number is 15 digits beginning and ending with 3.",
                    "الرقم الضريبي السعودي ١٥ رقمًا يبدأ وينتهي بالرقم ٣."),
                _ => ApiError.Create("registration_failed",
                    "Registration could not be completed.", "تعذّر إكمال التسجيل.")
            };
            return Results.Json(apiError, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        logger.LogInformation("Business registered, owner {UserId}.", userId);

        var challenge = await otp.IssueAsync(request.Owner.Email.Trim(), "register", ct);

        return Results.Ok(new
        {
            userId,
            branchId,
            challengeId = challenge.ChallengeId,
            expiresAtUtc = challenge.ExpiresAtUtc,
            devMode = challenge.IsDevCode
        });
    }

    /// <summary>
    /// Step one.
    ///
    /// In two-step mode this sends a code. In single-step mode it verifies a
    /// password and returns tokens directly. Which one runs is configuration,
    /// so switching back is a settings change rather than a code change.
    ///
    /// In both modes the response is deliberately the same whether or not the
    /// account exists. Saying "no such user" turns the login form into a tool
    /// for discovering which addresses are registered.
    /// </summary>
    private static async Task<IResult> RequestOtpAsync(
        LoginRequest request,
        IAuthStore store,
        IOtpService otp,
        IPasswordHasher hasher,
        IJwtTokenService tokens,
        IOptions<AuthOptions> authOptions,
        LicenseGuard licenseGuard,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Identifier))
        {
            return Results.BadRequest(ApiError.Create(
                "identifier_required",
                "Enter your email or mobile number.",
                "أدخل بريدك الإلكتروني أو رقم جوالك."));
        }

        var identifier = request.Identifier.Trim();

        await using var connection = await store.OpenAsync(ct);

        var user = await store.FindByIdentifierAsync(connection, identifier, ct);

        if (authOptions.Value.LoginMode == LoginMode.SinglePassword)
        {
            return await SignInWithPasswordAsync(
                store, connection, hasher, tokens, licenseGuard, user, request.Password, logger, ct);
        }

        /*----------------------------------------------- two-step: send a code */

        var challenge = await otp.IssueAsync(identifier, "login", ct);

        if (user is null)
        {
            logger.LogInformation("OTP requested for an unknown identifier.");
        }

        return Results.Ok(new
        {
            challengeId = challenge.ChallengeId,
            expiresAtUtc = challenge.ExpiresAtUtc,
            devMode = challenge.IsDevCode
        });
    }

    /// <summary>
    /// Single-step sign-in: a real password check, then tokens.
    ///
    /// This skips the second factor, not the first. The password is verified
    /// against the stored PBKDF2 hash exactly as it would be anywhere else, and
    /// an account with no password set cannot sign in this way at all — which
    /// is what stops a production database, where those hashes are NULL, from
    /// quietly accepting anyone should this mode ever be switched on there.
    /// </summary>
    private static async Task<IResult> SignInWithPasswordAsync(
        IAuthStore store,
        System.Data.Common.DbConnection connection,
        IPasswordHasher hasher,
        IJwtTokenService tokens,
        LicenseGuard licenseGuard,
        AuthUser? user,
        string? password,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return Results.BadRequest(ApiError.Create(
                "password_required", "Enter your password.", "أدخل كلمة المرور."));
        }

        /* One message for "no such account" and "wrong password" alike. */
        var invalid = Results.Json(ApiError.Create(
            "invalid_credentials",
            "That email or password is not correct.",
            "البريد الإلكتروني أو كلمة المرور غير صحيحة."), statusCode: 401);

        if (user is null || string.IsNullOrWhiteSpace(user.PasswordHash)
                         || string.IsNullOrWhiteSpace(user.PasswordSalt))
        {
            /* Spend roughly the time a real verification costs, so a reply does
               not arrive noticeably sooner for an account that does not exist. */
            hasher.Verify(password,
                "pbkdf2$210000$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                "AAAAAAAAAAAAAAAAAAAAAA==");

            logger.LogInformation("Failed sign-in: unknown account or no password set.");
            return invalid;
        }

        if (!hasher.Verify(password, user.PasswordHash, user.PasswordSalt))
        {
            logger.LogInformation("Failed sign-in for {UserId}.", user.UserId);
            return invalid;
        }

        if (RejectIfUnusable(user) is { } rejected) return rejected;

        var permissions = await store.PermissionsAsync(connection, user.UserId, ct);

        var branches = await store.BranchesAsync(connection, user.UserId, ct);

        /* The licence, last, immediately before a token exists. Checking after
           would mint a token that stays usable until it expires. */
        var (allowed, reason, _) = await licenseGuard.CheckAsync(connection, user.UserId, ct);
        if (!allowed)
        {
            return Results.Json(ApiError.Create("license_denied",
                reason ?? "Access denied.", reason ?? "تم رفض الوصول."), statusCode: 403);
        }

        logger.LogWarning(
            "Single-step password sign-in for {UserId}. The second factor was skipped.",
            user.UserId);

        return Results.Ok(await IssueSessionAsync(store, connection, tokens, user, permissions, branches, ct));
    }

    /// <summary>Step two: verify the code and issue a session.</summary>
    private static async Task<IResult> VerifyOtpAsync(
        OtpRequest request,
        IAuthStore store,
        IOtpService otp,
        IJwtTokenService tokens,
        LicenseGuard licenseGuard,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        var result = await otp.VerifyAsync(request.ChallengeId, request.Code, ct);

        if (result != OtpResult.Valid)
        {
            var (code, en, ar) = result switch
            {
                OtpResult.Expired => ("otp_expired",
                    "That code has expired. Request a new one.",
                    "انتهت صلاحية الرمز. اطلب رمزًا جديدًا."),
                OtpResult.TooManyAttempts => ("otp_attempts",
                    "Too many attempts. Request a new code.",
                    "محاولات كثيرة. اطلب رمزًا جديدًا."),
                _ => ("otp_invalid",
                    "That code is not correct.",
                    "الرمز غير صحيح.")
            };

            return Results.Json(ApiError.Create(code, en, ar), statusCode: 401);
        }

        await using var connection = await store.OpenAsync(ct);

        var identifier = await store.ChallengeIdentifierAsync(connection, request.ChallengeId, ct);

        if (identifier is null) return Results.Unauthorized();

        var user = await store.FindByIdentifierAsync(connection, identifier, ct);

        if (user is null) return Results.Unauthorized();

        if (!string.Equals(user.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(ApiError.Create(
                "account_suspended",
                "This account is not active. Speak to your manager.",
                "هذا الحساب غير نشط. راجع مديرك."), statusCode: 403);
        }

        // External accounts carry an expiry so a maintenance login does not
        // outlive the job it was created for.
        if (user.AccessExpiresOn is { } expires && expires < DateOnly.FromDateTime(DateTime.UtcNow))
        {
            return Results.Json(ApiError.Create(
                "account_expired",
                "Access for this account has expired.",
                "انتهت صلاحية الوصول لهذا الحساب."), statusCode: 403);
        }

        var permissions = await store.PermissionsAsync(connection, user.UserId, ct);

        var branches = await store.BranchesAsync(connection, user.UserId, ct);

        /* The licence, immediately before a token exists — the same
           check the password path makes, so neither flow is a way round it. */
        var (allowed, reason, _) = await licenseGuard.CheckAsync(connection, user.UserId, ct);
        if (!allowed)
        {
            return Results.Json(ApiError.Create("license_denied",
                reason ?? "Access denied.", reason ?? "تم رفض الوصول."), statusCode: 403);
        }

        return Results.Ok(await IssueSessionAsync(store, connection, tokens, user, permissions, branches, ct,
            request.RememberMe == true, authOptions.Value.RememberMeDays));
    }

    /// <summary>
    /// Mint a session and record the refresh token.
    ///
    /// Both login flows end here, so the response shape is identical whichever
    /// route the caller took and the frontend needs no branch of its own.
    /// </summary>
    private static async Task<SessionResponse> IssueSessionAsync(
        IAuthStore store,
        System.Data.Common.DbConnection connection,
        IJwtTokenService tokens,
        AuthUser user,
        IReadOnlyList<string> permissions,
        IReadOnlyList<string> branches,
        CancellationToken ct,
        bool remember = false,
        int rememberDays = 180)
    {
        var pair = tokens.Issue(user.UserId, user.Username, user.RoleId, permissions, branches);
        var expires = remember ? DateTime.UtcNow.AddDays(rememberDays) : pair.RefreshExpiresAtUtc;

        await store.SaveSessionAsync(connection, user.UserId, tokens.HashRefreshToken(pair.RefreshToken), expires, ct, remember);

        return new SessionResponse(
            pair.AccessToken,
            pair.RefreshToken,
            pair.AccessExpiresAtUtc,
            new UserSummary(user.UserId, user.Username, user.NameAr, user.NameEn,
                            user.RoleId, permissions, branches));
    }

    /// <summary>
    /// Whether the account may sign in at all, whichever flow is in use.
    /// </summary>
    private static IResult? RejectIfUnusable(AuthUser user)
    {
        if (!string.Equals(user.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(ApiError.Create(
                "account_suspended",
                "This account is not active. Speak to your manager.",
                "هذا الحساب غير نشط. راجع مديرك."), statusCode: 403);
        }

        if (user.AccessExpiresOn is { } expires && expires < DateOnly.FromDateTime(DateTime.UtcNow))
        {
            return Results.Json(ApiError.Create(
                "account_expired",
                "Access for this account has expired.",
                "انتهت صلاحية الوصول لهذا الحساب."), statusCode: 403);
        }

        return null;
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        IAuthStore store,
        IJwtTokenService tokens,
        LicenseGuard licenseGuard,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(request.RefreshToken);

        await using var connection = await store.OpenAsync(ct);

        var userId = await store.ActiveRefreshTokenUserAsync(connection, hash, ct);

        if (userId is null) return Results.Unauthorized();

        var user = await store.FindByIdAsync(connection, userId, ct);
        if (user is null) return Results.Unauthorized();

        if (!string.Equals(user.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Unauthorized();
        }

        var permissions = await store.PermissionsAsync(connection, userId, ct);

        var branches = await store.BranchesAsync(connection, userId, ct);

        /*
         * Refresh is a sign-in too.
         *
         * Without this, a refresh token issued before a licence expired
         * keeps minting access tokens for fourteen days afterwards — and a
         * blocked business keeps working until its tokens run out.
         */
        var (allowed, reason, _) = await licenseGuard.CheckAsync(connection, user.UserId, ct);
        if (!allowed)
        {
            return Results.Json(ApiError.Create("license_denied",
                reason ?? "Access denied.", reason ?? "تم رفض الوصول."), statusCode: 403);
        }

        var pair = tokens.Issue(user.UserId, user.Username, user.RoleId, permissions, branches);

        // Rotate: the old token is revoked as the new one is issued, so a
        // stolen refresh token is usable at most once before it stops working.
        /* A remembered session renews for another RememberMeDays on every refresh: in daily use it never runs out. */
        var remember = await store.IsRememberedAsync(connection, hash, ct);
        var expires = remember ? DateTime.UtcNow.AddDays(authOptions.Value.RememberMeDays) : pair.RefreshExpiresAtUtc;
        await store.RotateRefreshTokenAsync(connection, hash, tokens.HashRefreshToken(pair.RefreshToken), userId, expires, ct, remember);

        return Results.Ok(new SessionResponse(
            pair.AccessToken, pair.RefreshToken, pair.AccessExpiresAtUtc,
            new UserSummary(user.UserId, user.Username, user.NameAr, user.NameEn,
                            user.RoleId, permissions, branches)));
    }

    private static async Task<IResult> LogoutAsync(
        RefreshRequest request,
        IAuthStore store,
        IJwtTokenService tokens,
        CancellationToken ct)
    {
        await using var connection = await store.OpenAsync(ct);
        await store.RevokeRefreshTokenAsync(connection, tokens.HashRefreshToken(request.RefreshToken), ct);

        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        ICurrentUser current,
        IAuthStore store,
        CancellationToken ct = default)
    {
        await using var connection = await store.OpenAsync(ct);
        var user = await store.FindByIdAsync(connection, current.UserId, ct);

        if (user is null) return Results.Unauthorized();

        return Results.Ok(new UserSummary(
            user.UserId, user.Username, user.NameAr, user.NameEn, user.RoleId,
            current.Permissions.ToList(), current.BranchIds.ToList()));
    }
}
