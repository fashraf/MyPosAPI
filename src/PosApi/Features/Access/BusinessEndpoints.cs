using PosApi.Data;
using SqlKata.Execution;

namespace PosApi.Features.Access;

/// <summary>
/// The signed-in business itself: its names, tax numbers, branches and people. A new device (or a brand-new SaaS
/// client) starts from this, never from someone else's starter data.
/// </summary>
public static class BusinessEndpoints
{
    public static void MapBusinessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/business", async (IDb db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var q = db.Query(conn);
            var company = (await q.Query("core.company").OrderBy("company_id").GetAsync(cancellationToken: ct))
                .Cast<IDictionary<string, object?>>().FirstOrDefault();
            var branches = (await q.Query("core.branch").OrderBy("branch_id").Select("branch_id", "code", "name_ar", "name_en").GetAsync(cancellationToken: ct))
                .Cast<IDictionary<string, object?>>();
            var users = (await q.Query("core.app_user").OrderBy("user_id").Select("user_id", "username", "name_ar", "name_en", "email", "phone", "role_id", "status").GetAsync(cancellationToken: ct))
                .Cast<IDictionary<string, object?>>();
            string? S(IDictionary<string, object?>? r, string k) => r is not null && r.TryGetValue(k, out var v) ? v?.ToString() : null;
            return Results.Ok(new
            {
                nameAr = S(company, "name_ar") ?? "", nameEn = S(company, "name_en") ?? "",
                crNumber = S(company, "cr_number") ?? "", vatNumber = S(company, "vat_number") ?? "", city = S(company, "city") ?? "",
                branches = branches.Select(b => new { id = S(b, "branch_id"), code = S(b, "code"), nameAr = S(b, "name_ar"), nameEn = S(b, "name_en"), city = S(company, "city") }),
                users = users.Select(u => new { id = S(u, "user_id"), username = S(u, "username"), nameAr = S(u, "name_ar"), nameEn = S(u, "name_en"),
                    email = S(u, "email"), phone = S(u, "phone"), roleId = S(u, "role_id"), status = S(u, "status") }),
            });
        }).RequireAuthorization().RequireRateLimiting("standard").WithTags("Business");
    }
}
