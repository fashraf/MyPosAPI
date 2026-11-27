using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;
using SqlKata.Execution;

namespace PosApi.Features.Store;

/// <summary>
/// Shared store for the modules that are not yet ported to their own tables. Each module ("slice") is one JSON
/// document with a revision. A device writes with the revision it last saw; if someone else saved in between it gets
/// 409 with the current copy, so no one silently overwrites another device's change.
/// </summary>
public static partial class TenantStoreEndpoints
{
    private const int MaxBytes = 4 * 1024 * 1024;

    /* Modules the server already owns in real tables are refused here, so the two copies can never disagree. */
    private static readonly HashSet<string> ServerOwned = new(StringComparer.OrdinalIgnoreCase)
        { "sales", "restaurantSessions", "catalog", "kitchen" };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex NameRule();

    public sealed record PutBody(long BaseRevision, JsonNode? Data);

    public static void MapTenantStoreEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/store").WithTags("Store").RequireAuthorization().RequireRateLimiting("standard");

        /* Revisions only: a cheap call devices make every few seconds to see what changed. */
        g.MapGet("/revisions", async (IDb db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await db.Query(conn).Query("core.tenant_store").Select("slice_name", "revision").GetAsync(cancellationToken: ct);
            return Results.Ok(rows.Cast<IDictionary<string, object?>>()
                .ToDictionary(r => (string)r["slice_name"]!, r => Convert.ToInt64(r["revision"])));
        });

        /* Full copies; ?names=a,b limits the list. */
        g.MapGet("/slices", async (IDb db, string? names, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var q = db.Query(conn).Query("core.tenant_store");
            var wanted = (names ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (wanted.Length > 0) q = q.WhereIn("slice_name", wanted);
            var rows = await q.GetAsync(cancellationToken: ct);
            return Results.Ok(rows.Cast<IDictionary<string, object?>>().Select(Shape));
        });

        g.MapPut("/slices/{name}", async (string name, PutBody body, IDb db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!NameRule().IsMatch(name) || ServerOwned.Contains(name))
                return Results.Json(ApiError.Create("bad_slice", "This module cannot be saved here.", "لا يمكن حفظ هذه الوحدة هنا."), statusCode: 422);
            var json = body.Data?.ToJsonString() ?? "null";
            if (json.Length > MaxBytes)
                return Results.Json(ApiError.Create("too_large", "This module's data is too large to save.", "بيانات هذه الوحدة كبيرة جدا."), statusCode: 413);

            var now = DateTime.UtcNow;
            var who = user.UserId;
            var (status, row) = await db.InTransactionAsync(async (conn, tx) =>
            {
                var q = db.Query(conn);
                var current = (await q.Query("core.tenant_store").Where("slice_name", name).GetAsync(tx))
                    .Cast<IDictionary<string, object?>>().FirstOrDefault();
                var currentRev = current is null ? 0L : Convert.ToInt64(current["revision"]);
                if (currentRev != body.BaseRevision) return (409, current);

                var values = new Dictionary<string, object?>
                    { ["revision"] = currentRev + 1, ["data_json"] = json, ["updated_at_utc"] = now, ["updated_by"] = who };
                if (current is null)
                {
                    values["slice_name"] = name;
                    await q.Query("core.tenant_store").InsertAsync(values, tx);
                }
                else
                {
                    /* The revision check in the WHERE also guards against a save landing between our read and write. */
                    var n = await q.Query("core.tenant_store").Where("slice_name", name).Where("revision", currentRev).UpdateAsync(values, tx);
                    if (n == 0) return (409, current);
                }
                values["slice_name"] = name;
                return (200, (IDictionary<string, object?>?)values);
            }, ct);

            return status == 409
                ? Results.Json(new { code = "conflict", messageEn = "Another device saved this first.", current = row is null ? null : Shape(row) }, statusCode: 409)
                : Results.Ok(Shape(row!));
        });
    }

    private static object Shape(IDictionary<string, object?> r) => new
    {
        name = (string)r["slice_name"]!,
        revision = Convert.ToInt64(r["revision"]),
        data = JsonNode.Parse((string)r["data_json"]!),
        updatedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(r["updated_at_utc"]), DateTimeKind.Utc),
    };
}
