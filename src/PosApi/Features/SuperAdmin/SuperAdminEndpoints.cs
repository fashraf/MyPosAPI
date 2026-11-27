using System.Diagnostics;
using System.Text.RegularExpressions;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;
using PosApi.Data.Migrations;
using PosApi.Features.Access;
using PosApi.Tenancy;
using SqlKata.Execution;

namespace PosApi.Features.SuperAdmin;

/// <summary>
/// POSSA, the super admin API (admin.wateeq.com/api/sa). Creates clients, points each at its own database, prepares
/// that database, creates the client's owner account, pauses or resumes a client, and shows each client's health.
/// Only works in SaaS mode (Tenancy:Mode=multi) with a control database.
/// </summary>
public static partial class SuperAdminEndpoints
{
    public const string Role = "super_admin";
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "admin", "www", "api", "app", "mail", "staging", "status", "possa", "support", "help", "docs" };

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,38}[a-z0-9]\z")] private static partial Regex SlugRule();

    public sealed record LoginBody(string Email, string Password);
    public sealed record DbBody(string Host, int Port, string Database, string User, string? Password);
    public sealed record CreateBody(string Slug, string Name, string? CustomHost, string? Plan, DbBody Db,
        string? ContactName, string? ContactEmail, string? ContactPhone, string? Notes);
    public sealed record UpdateBody(string? Name, string? Status, string? Plan, string? CustomHost, string? Notes,
        string? ContactName, string? ContactEmail, string? ContactPhone, DbBody? Db);
    public sealed record OwnerBody(string BusinessNameAr, string BusinessNameEn, string? City, string? CrNumber, string? VatNumber,
        string OwnerName, string OwnerEmail, string? OwnerMobile);
    public sealed record PasswordBody(string Current, string Next);

    public static void MapSuperAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var open = app.MapGroup("/api/sa").WithTags("Super admin").RequireRateLimiting("auth");
        open.MapPost("/auth/login", LoginAsync).AllowAnonymous();

        var g = app.MapGroup("/api/sa").WithTags("Super admin").RequireRateLimiting("standard")
            .RequireAuthorization(p => p.RequireRole(Role));

        g.MapGet("/me", (ICurrentUser user) => Results.Ok(new { email = user.Username, role = Role }));
        g.MapPost("/me/password", ChangePasswordAsync);
        g.MapGet("/overview", OverviewAsync);
        g.MapGet("/tenants", ListAsync);
        g.MapGet("/tenants/{id:long}", GetAsync);
        g.MapPost("/tenants", CreateAsync);
        g.MapPatch("/tenants/{id:long}", UpdateAsync);
        g.MapPost("/test-connection", (DbBody body, ControlStore control, CancellationToken ct) => TestAsync(control, body, ct));
        g.MapPost("/tenants/{id:long}/migrate", MigrateAsync);
        g.MapPost("/tenants/{id:long}/owner", OwnerAsync);
        g.MapGet("/tenants/{id:long}/health", HealthAsync);
        g.MapGet("/events", EventsAsync);
    }

    /* ------------------------------------------------------------------ sign in */

    private static async Task<IResult> LoginAsync(LoginBody body, ControlStore control, IPasswordHasher hasher, IJwtTokenService tokens, CancellationToken ct)
    {
        if (!control.Options.IsMulti) return Error(404, "not_enabled", "SaaS mode is not switched on for this API.", "وضع الخدمة السحابية غير مفعل.");
        await using var c = await control.OpenAsync(ct);
        var email = (body.Email ?? "").Trim().ToLowerInvariant();
        var row = (await control.Query(c).Query("control.admin_user").Where("email", email).GetAsync(cancellationToken: ct))
            .Cast<IDictionary<string, object?>>().FirstOrDefault();
        if (row is null || !hasher.Verify(body.Password ?? "", (string)row["password_hash"]!, (string)row["password_salt"]!))
            return Error(401, "bad_credentials", "Wrong email or password.", "البريد أو كلمة المرور غير صحيحة.");
        await control.Query(c).Query("control.admin_user").Where("admin_id", row["admin_id"]).UpdateAsync(
            new Dictionary<string, object?> { ["last_login_at_utc"] = DateTime.UtcNow }, cancellationToken: ct);
        TenantContext.Admin = true;
        var pair = tokens.Issue(Convert.ToString(row["admin_id"])!, email, Role, ["sa.all"], []);
        await control.LogAsync(null, "admin_login", null, email, ct);
        return Results.Ok(new { accessToken = pair.AccessToken, expiresAtUtc = pair.AccessExpiresAtUtc, name = (string)row["name"]!, email });
    }

    private static async Task<IResult> ChangePasswordAsync(PasswordBody body, ControlStore control, IPasswordHasher hasher, ICurrentUser user, CancellationToken ct)
    {
        if ((body.Next ?? "").Length < 10) return Error(422, "weak_password", "Use at least 10 characters.", "استخدم 10 أحرف على الأقل.");
        await using var c = await control.OpenAsync(ct);
        var q = control.Query(c).Query("control.admin_user").Where("email", user.Username);
        var row = (await q.Clone().GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();
        if (row is null || !hasher.Verify(body.Current ?? "", (string)row["password_hash"]!, (string)row["password_salt"]!))
            return Error(401, "bad_credentials", "The current password is wrong.", "كلمة المرور الحالية غير صحيحة.");
        var (hash, salt) = hasher.Hash(body.Next!);
        await q.UpdateAsync(new Dictionary<string, object?> { ["password_hash"] = hash, ["password_salt"] = salt }, cancellationToken: ct);
        return Results.NoContent();
    }

    /* ------------------------------------------------------------------ clients */

    private static async Task<IResult> OverviewAsync(ControlStore control, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var rows = (await control.Query(c).Query("control.tenant").Select("status", "schema_version").GetAsync(cancellationToken: ct))
            .Cast<IDictionary<string, object?>>().ToList();
        var latest = SchemaMigrator.LatestClientVersion();
        return Results.Ok(new
        {
            clients = rows.Count,
            active = rows.Count(r => (string)r["status"]! == "active"),
            suspended = rows.Count(r => (string)r["status"]! == "suspended"),
            settingUp = rows.Count(r => (string)r["status"]! == "setup"),
            behind = rows.Count(r => r["schema_version"] is null || Convert.ToInt64(r["schema_version"]) < latest),
            latestSchema = latest,
            apiVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev",
            rootDomain = control.Options.RootDomain,
        });
    }

    private static async Task<IResult> ListAsync(ControlStore control, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var rows = await control.Query(c).Query("control.tenant").OrderBy("tenant_id").GetAsync(cancellationToken: ct);
        var latest = SchemaMigrator.LatestClientVersion();
        return Results.Ok(rows.Cast<IDictionary<string, object?>>().Select(r => Shape(r, control, latest)));
    }

    private static async Task<IResult> GetAsync(long id, ControlStore control, CancellationToken ct)
    {
        var row = await Find(control, id, ct);
        return row is null ? NotFound() : Results.Ok(Shape(row, control, SchemaMigrator.LatestClientVersion()));
    }

    private static async Task<IResult> CreateAsync(CreateBody body, ControlStore control, ICurrentUser user, CancellationToken ct)
    {
        var slug = (body.Slug ?? "").Trim().ToLowerInvariant();
        if (!SlugRule().IsMatch(slug) || Reserved.Contains(slug))
            return Error(422, "bad_slug", "Use 3-40 lowercase letters, digits or dashes (e.g. pos1). Some names are reserved.", "استخدم 3-40 حرفًا إنجليزيًا صغيرًا أو أرقامًا أو شرطات (مثل pos1).");
        if (string.IsNullOrWhiteSpace(body.Name)) return Error(422, "name_required", "Enter the client's name.", "أدخل اسم العميل.");
        if (string.IsNullOrWhiteSpace(body.Db.Password)) return Error(422, "password_required", "Enter the database password.", "أدخل كلمة مرور قاعدة البيانات.");

        var test = await Probe(control, body.Db, ct);
        if (test.Error is not null) return Error(422, "db_unreachable", $"Could not connect to that database: {test.Error}", $"تعذر الاتصال بقاعدة البيانات: {test.Error}");

        await using var c = await control.OpenAsync(ct);
        var q = control.Query(c);
        if (await q.Query("control.tenant").Where("slug", slug).ExistsAsync(cancellationToken: ct))
            return Error(409, "slug_taken", $"{slug} is already used by another client.", "هذا الاسم مستخدم لعميل آخر.");
        var now = DateTime.UtcNow;
        var id = await q.Query("control.tenant").InsertGetIdAsync<long>(new Dictionary<string, object?>
        {
            ["slug"] = slug, ["name"] = body.Name.Trim(), ["custom_host"] = Blank(body.CustomHost)?.ToLowerInvariant(), ["status"] = "setup", ["plan"] = Blank(body.Plan),
            ["db_host"] = body.Db.Host.Trim(), ["db_port"] = body.Db.Port, ["db_name"] = body.Db.Database.Trim(), ["db_user"] = body.Db.User.Trim(),
            ["db_password_enc"] = control.Protect(body.Db.Password!), ["contact_name"] = Blank(body.ContactName), ["contact_email"] = Blank(body.ContactEmail),
            ["contact_phone"] = Blank(body.ContactPhone), ["notes"] = Blank(body.Notes), ["created_at_utc"] = now, ["updated_at_utc"] = now,
        }, cancellationToken: ct);
        await control.LogAsync(id, "created", $"{slug}.{control.Options.RootDomain} → {body.Db.Host}/{body.Db.Database}", user.Username, ct);
        control.Forget();
        return Results.Ok(Shape((await Find(control, id, ct))!, control, SchemaMigrator.LatestClientVersion()));
    }

    private static async Task<IResult> UpdateAsync(long id, UpdateBody body, ControlStore control, ICurrentUser user, CancellationToken ct)
    {
        var row = await Find(control, id, ct);
        if (row is null) return NotFound();
        if (body.Status is not null and not ("setup" or "active" or "suspended"))
            return Error(422, "bad_status", "Status is setup, active or suspended.", "الحالة: إعداد أو نشط أو موقوف.");
        var values = new Dictionary<string, object?> { ["updated_at_utc"] = DateTime.UtcNow };
        if (body.Name is not null) values["name"] = body.Name.Trim();
        if (body.Status is not null) values["status"] = body.Status;
        if (body.Plan is not null) values["plan"] = Blank(body.Plan);
        if (body.CustomHost is not null) values["custom_host"] = Blank(body.CustomHost)?.ToLowerInvariant();
        if (body.Notes is not null) values["notes"] = Blank(body.Notes);
        if (body.ContactName is not null) values["contact_name"] = Blank(body.ContactName);
        if (body.ContactEmail is not null) values["contact_email"] = Blank(body.ContactEmail);
        if (body.ContactPhone is not null) values["contact_phone"] = Blank(body.ContactPhone);
        if (body.Db is not null)
        {
            /* A new password is optional when only the host or name changes. */
            var password = string.IsNullOrWhiteSpace(body.Db.Password)
                ? SecretBox.Unprotect((string)row["db_password_enc"]!, control.Configuration, ControlStore.SecretPurpose)
                : body.Db.Password!;
            var test = await Probe(control, body.Db with { Password = password }, ct);
            if (test.Error is not null) return Error(422, "db_unreachable", $"Could not connect to that database: {test.Error}", $"تعذر الاتصال بقاعدة البيانات: {test.Error}");
            values["db_host"] = body.Db.Host.Trim(); values["db_port"] = body.Db.Port; values["db_name"] = body.Db.Database.Trim();
            values["db_user"] = body.Db.User.Trim(); values["db_password_enc"] = control.Protect(password);
        }
        await using var c = await control.OpenAsync(ct);
        await control.Query(c).Query("control.tenant").Where("tenant_id", id).UpdateAsync(values, cancellationToken: ct);
        await control.LogAsync(id, body.Status is not null ? body.Status : "updated", string.Join(", ", values.Keys.Where(k => k != "updated_at_utc" && k != "db_password_enc")), user.Username, ct);
        control.Forget();
        return Results.Ok(Shape((await Find(control, id, ct))!, control, SchemaMigrator.LatestClientVersion()));
    }

    private static async Task<IResult> TestAsync(ControlStore control, DbBody body, CancellationToken ct)
    {
        var (version, error, ms, tables) = await Probe(control, body, ct);
        return error is null
            ? Results.Ok(new { ok = true, serverVersion = version, milliseconds = ms, tables })
            : Results.Json(new { ok = false, code = "db_unreachable", messageEn = $"Could not connect: {error}", messageAr = $"تعذر الاتصال: {error}" }, statusCode: 422);
    }

    /// <summary>Brings this client's database up to date (all migrations).</summary>
    private static async Task<IResult> MigrateAsync(long id, ControlStore control, IConfiguration config, ICurrentUser user, CancellationToken ct)
    {
        var row = await Find(control, id, ct);
        if (row is null) return NotFound();
        var tenant = control.ToTenant(row);
        var log = new StringWriter();
        var original = Console.Out;
        bool ok;
        /* The migration runner writes to the console; capture it so POSSA can show what happened. */
        lock (MigrationLock)
        {
            Console.SetOut(log);
            try { ok = SchemaMigrator.Migrate(config, tenant.ConnectionString, SchemaMigrator.ClientNamespace, $"client {tenant.Slug}"); }
            finally { Console.SetOut(original); }
        }
        if (ok) await SchemaMigrator.MarkMigrated(control, id);
        var text = log.ToString();
        await control.LogAsync(id, ok ? "migrated" : "migration_failed", text.Length > 3900 ? text[^3900..] : text, user.Username, ct);
        return ok
            ? Results.Ok(new { ok, schemaVersion = SchemaMigrator.LatestClientVersion(), log = Tail(text) })
            : Results.Json(new { ok, code = "migration_failed", messageEn = "The database could not be prepared. See the log.", messageAr = "تعذر تجهيز قاعدة البيانات. راجع السجل.", log = Tail(text) }, statusCode: 422);
    }

    private static readonly object MigrationLock = new();

    /// <summary>The client's business, first branch and owner account, in the client's own database.</summary>
    private static async Task<IResult> OwnerAsync(long id, OwnerBody body, ControlStore control, IAuthStore store, IDb db, ICurrentUser user, CancellationToken ct)
    {
        var row = await Find(control, id, ct);
        if (row is null) return NotFound();
        if (row["schema_version"] is null) return Error(409, "not_migrated", "Prepare the database first.", "جهّز قاعدة البيانات أولًا.");
        if (string.IsNullOrWhiteSpace(body.OwnerEmail) || string.IsNullOrWhiteSpace(body.BusinessNameAr) || string.IsNullOrWhiteSpace(body.BusinessNameEn))
            return Error(422, "missing", "Business names (Arabic and English) and the owner's email are required.", "اسما المنشأة بالعربية والإنجليزية وبريد المالك مطلوبة.");

        var tenant = control.ToTenant(row);
        var before = TenantContext.Current;
        TenantContext.Current = tenant;
        try
        {
            var (error, userId, branchId) = await store.RegisterBusinessAsync(new RegistrationInput(
                body.BusinessNameAr.Trim(), body.BusinessNameEn.Trim(), Blank(body.CrNumber), Blank(body.VatNumber), Blank(body.City),
                body.BusinessNameAr.Trim(), body.BusinessNameEn.Trim(), body.OwnerName.Trim(), body.OwnerName.Trim(),
                body.OwnerEmail.Trim().ToLowerInvariant(), Blank(body.OwnerMobile)), ct);
            if (error != RegistrationError.None)
                return Error(409, error.ToString(), $"The owner could not be created ({error}). An owner with that email may already exist.", "تعذر إنشاء المالك. قد يكون البريد مستخدمًا.");
            /* Standard roles with every permission the app uses (restaurant day, kitchen, finance …). No demo items or people. */
            var starter = Path.Combine(AppContext.BaseDirectory, "seed", "standard-roles.json");
            if (File.Exists(starter)) await PosApi.Data.Seeding.TenantSeeder.RunAsync(db, starter, TextWriter.Null);
            await using var c = await control.OpenAsync(ct);
            await control.Query(c).Query("control.tenant").Where("tenant_id", id).UpdateAsync(new Dictionary<string, object?>
                { ["status"] = "active", ["contact_email"] = row["contact_email"] ?? body.OwnerEmail, ["updated_at_utc"] = DateTime.UtcNow }, cancellationToken: ct);
            await control.LogAsync(id, "owner_created", $"{body.OwnerEmail} (user {userId}, branch {branchId})", user.Username, ct);
            control.Forget();
            return Results.Ok(new { ok = true, userId, branchId, signInAt = $"https://{tenant.Slug}.{control.Options.RootDomain}" });
        }
        finally { TenantContext.Current = before; }
    }

    /// <summary>Is the client alive: database reachable, schema version, activity and errors in the last day.</summary>
    private static async Task<IResult> HealthAsync(long id, ControlStore control, IDb db, CancellationToken ct)
    {
        var row = await Find(control, id, ct);
        if (row is null) return NotFound();
        var tenant = control.ToTenant(row);
        var before = TenantContext.Current;
        TenantContext.Current = tenant;
        var watch = Stopwatch.StartNew();
        try
        {
            await using var conn = await db.OpenAsync(ct);
            var q = db.Query(conn);
            var since = DateTime.UtcNow.AddHours(-24);
            async Task<long> Count(Func<SqlKata.Query> make) { try { return Convert.ToInt64(await make().AsCount().FirstAsync<long>(cancellationToken: ct)); } catch { return -1; } }
            var schema = await Try(async () => await q.Query("VersionInfo").MaxAsync<long?>("Version", cancellationToken: ct)) ?? 0;
            var users = await Count(() => q.Query("core.app_user"));
            var salesToday = await Count(() => q.Query("sales.sale").Where("sold_at_utc", ">=", since));
            var errors = await Count(() => q.Query("audit.request_timing").Where("succeeded", false).Where("status_code", ">=", 500).Where("occurred_at_utc", ">=", since));
            var lastSale = await Try(async () => await q.Query("sales.sale").MaxAsync<DateTime?>("sold_at_utc", cancellationToken: ct));
            object? openDay = await Try(async () => await q.Query("sales.restaurant_session").Where("status", "open").Select("session_no", "opened_at_utc").FirstOrDefaultAsync(cancellationToken: ct));
            return Results.Ok(new
            {
                ok = true, milliseconds = watch.ElapsedMilliseconds, schemaVersion = schema, latestSchema = SchemaMigrator.LatestClientVersion(),
                users, salesLast24h = salesToday, serverErrorsLast24h = errors,
                lastSaleUtc = lastSale is null ? (DateTime?)null : DateTime.SpecifyKind(lastSale.Value, DateTimeKind.Utc),
                restaurantOpen = openDay is not null,
            });
        }
        catch (Exception ex)
        {
            return Results.Ok(new { ok = false, milliseconds = watch.ElapsedMilliseconds, error = ex.Message });
        }
        finally { TenantContext.Current = before; }
    }

    private static async Task<IResult> EventsAsync(ControlStore control, long? tenantId, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        var q = control.Query(c).Query("control.tenant_event").OrderByDesc("event_id").Limit(200);
        if (tenantId is not null) q = q.Where("tenant_id", tenantId);
        var rows = await q.GetAsync(cancellationToken: ct);
        return Results.Ok(rows.Cast<IDictionary<string, object?>>().Select(r => new
        {
            id = Convert.ToInt64(r["event_id"]), tenantId = r["tenant_id"] is null ? (long?)null : Convert.ToInt64(r["tenant_id"]),
            kind = (string)r["kind"]!, detail = r["detail"] as string, actor = r["actor"] as string,
            atUtc = DateTime.SpecifyKind(Convert.ToDateTime(r["at_utc"]), DateTimeKind.Utc),
        }));
    }

    /* ------------------------------------------------------------------ helpers */

    private static async Task<(string? Version, string? Error, long Ms, int Tables)> Probe(ControlStore control, DbBody body, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(body.Host) || string.IsNullOrWhiteSpace(body.Database) || string.IsNullOrWhiteSpace(body.User))
                return (null, "host, database and user are required", 0, 0);
            var cs = control.BuildConnectionString(new TenantDb(body.Host.Trim(), body.Port <= 0 ? 5432 : body.Port, body.Database.Trim(), body.User.Trim(), body.Password ?? ""));
            await using var c = control.CreateConnection(cs);
            await c.OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema NOT IN ('information_schema','pg_catalog','sys')";
            var tables = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            return (c.ServerVersion, null, watch.ElapsedMilliseconds, tables);
        }
        catch (Exception ex) { return (null, ex.Message, watch.ElapsedMilliseconds, 0); }
    }

    private static async Task<IDictionary<string, object?>?> Find(ControlStore control, long id, CancellationToken ct)
    {
        await using var c = await control.OpenAsync(ct);
        return (await control.Query(c).Query("control.tenant").Where("tenant_id", id).GetAsync(cancellationToken: ct))
            .Cast<IDictionary<string, object?>>().FirstOrDefault();
    }

    private static object Shape(IDictionary<string, object?> r, ControlStore control, long latest)
    {
        var slug = (string)r["slug"]!;
        var schema = r["schema_version"] is null ? (long?)null : Convert.ToInt64(r["schema_version"]);
        return new
        {
            id = Convert.ToInt64(r["tenant_id"]), slug, name = (string)r["name"]!, status = (string)r["status"]!, plan = r["plan"] as string,
            url = $"https://{(r["custom_host"] as string) ?? $"{slug}.{control.Options.RootDomain}"}", customHost = r["custom_host"] as string,
            db = new { host = (string)r["db_host"]!, port = Convert.ToInt32(r["db_port"]), database = (string)r["db_name"]!, user = (string)r["db_user"]! },
            contactName = r["contact_name"] as string, contactEmail = r["contact_email"] as string, contactPhone = r["contact_phone"] as string,
            notes = r["notes"] as string, schemaVersion = schema, upToDate = schema is not null && schema >= latest,
            migratedAtUtc = r["migrated_at_utc"] is null ? (DateTime?)null : DateTime.SpecifyKind(Convert.ToDateTime(r["migrated_at_utc"]), DateTimeKind.Utc),
            createdAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(r["created_at_utc"]), DateTimeKind.Utc),
        };
    }

    private static async Task<T?> Try<T>(Func<Task<T?>> work) { try { return await work(); } catch { return default; } }
    private static string Tail(string text) => text.Length > 4000 ? text[^4000..] : text;
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static IResult NotFound() => Error(404, "not_found", "That client does not exist.", "هذا العميل غير موجود.");
    private static IResult Error(int status, string code, string en, string ar) => Results.Json(ApiError.Create(code, en, ar), statusCode: status);
}
