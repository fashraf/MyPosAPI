using System.Text.Json;
using System.Text.Json.Nodes;
using SqlKata.Execution;

namespace PosApi.Data.Seeding;

/// <summary>
/// Loads a business's setup data (branches, users, roles and permissions, categories, print groups, catalog items
/// with their combos and options) from a JSON file exported from the front end's setup.
///
/// Additive and idempotent: a row is inserted only when its id does not exist yet, and nothing that exists is ever
/// changed. Running it on every deploy is safe; edits made in the app are never overwritten. It never creates
/// sales, sessions or any other activity: those start empty.
///
/// Run: <c>dotnet PosApi.dll --seed seed/tenant-alnoor.json</c> (after <c>--migrate</c>).
/// </summary>
public static class TenantSeeder
{
    public static async Task<int> RunAsync(IDb db, string path, TextWriter log, CancellationToken ct = default)
    {
        if (!File.Exists(path)) { await log.WriteLineAsync($"Seed file not found: {path}"); return 1; }
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject();
        var now = DateTime.UtcNow;
        var added = new Dictionary<string, int>();
        void Count(string what) => added[what] = added.GetValueOrDefault(what) + 1;

        await db.InTransactionAsync<int>(async (conn, tx) =>
        {
            var q = db.Query(conn);
            async Task<bool> Exists(string table, string key, object value) =>
                await q.Query(table).Where(key, value).ExistsAsync(tx);

            /* Older QA runs registered a placeholder "QA Business"; the setup company replaces it (receipts and the
               ZATCA stamp carry the company's name and VAT number). */
            if (root["company"] is JsonObject seedCo && await q.Query("core.company").Where("name_en", "QA Business").ExistsAsync(tx))
            {
                await q.Query("core.company").Where("name_en", "QA Business").UpdateAsync(new Dictionary<string, object?>
                {
                    ["name_ar"] = S(seedCo, "nameAr") ?? S(seedCo, "nameEn") ?? "", ["name_en"] = S(seedCo, "nameEn") ?? "",
                    ["vat_number"] = S(seedCo, "vatNumber"), ["cr_number"] = S(seedCo, "crNumber"), ["city"] = S(seedCo, "city"),
                    ["updated_at_utc"] = now,
                }, tx);
                Count("company (replaced QA placeholder)");
            }

            /* Company: only when the database has none (one business per database). */
            if (root["company"] is JsonObject co && !await q.Query("core.company").ExistsAsync(tx))
            {
                await q.Query("core.company").InsertAsync(new Dictionary<string, object?>
                {
                    ["name_ar"] = S(co, "nameAr") ?? S(co, "nameEn") ?? "", ["name_en"] = S(co, "nameEn") ?? "",
                    ["description_en"] = S(co, "description"), ["logo_url"] = S(co, "logoUrl"),
                    ["cr_number"] = S(co, "crNumber"), ["vat_number"] = S(co, "vatNumber"), ["phone"] = S(co, "phone"),
                    ["email"] = S(co, "email"), ["website"] = S(co, "website"), ["city"] = S(co, "city"),
                    ["country"] = S(co, "country") ?? "SA", ["created_at_utc"] = now, ["updated_at_utc"] = now,
                }, tx);
                Count("company");
            }

            foreach (var b in Arr(root, "branches"))
            {
                var id = S(b, "id")!;
                if (await Exists("core.branch", "branch_id", id)) continue;
                await q.Query("core.branch").InsertAsync(new Dictionary<string, object?>
                {
                    ["branch_id"] = id, ["code"] = S(b, "code") ?? id, ["name_ar"] = S(b, "nameAr") ?? "", ["name_en"] = S(b, "nameEn") ?? "",
                    ["phone"] = S(b, "phone"), ["address_en"] = S(b, "city"), ["is_active"] = S(b, "status") != "inactive",
                    ["created_at_utc"] = now, ["updated_at_utc"] = now,
                }, tx);
                Count("branches");
            }

            /* Permissions used by the front end that the server does not know yet, then roles and their grants. */
            var roles = Arr(root, "roles").ToList();
            foreach (var key in roles.SelectMany(r => Arr(r, "permissions").Select(p => p?.GetValue<string>())).Where(k => k is not null).Distinct())
            {
                if (await Exists("core.permission", "permission_key", key!)) continue;
                var parts = key!.Split('.', 2);
                await q.Query("core.permission").InsertAsync(new Dictionary<string, object?>
                {
                    ["permission_key"] = key, ["module"] = parts[0], ["action"] = parts.Length > 1 ? parts[1] : "view",
                    ["sensitivity"] = Sensitivity(key),
                }, tx);
                Count("permissions");
            }
            foreach (var r in roles)
            {
                var id = S(r, "id")!;
                if (!await Exists("core.role", "role_id", id))
                {
                    await q.Query("core.role").InsertAsync(new Dictionary<string, object?>
                    {
                        ["role_id"] = id, ["name_ar"] = S(r, "nameAr") ?? id, ["name_en"] = S(r, "nameEn") ?? id,
                        ["is_built_in"] = true, ["is_external"] = false, ["created_at_utc"] = now, ["updated_at_utc"] = now,
                    }, tx);
                    Count("roles");
                }
                foreach (var key in Arr(r, "permissions").Select(p => p!.GetValue<string>()).Distinct())
                {
                    if (await q.Query("core.role_permission").Where("role_id", id).Where("permission_key", key).ExistsAsync(tx)) continue;
                    await q.Query("core.role_permission").InsertAsync(new { role_id = id, permission_key = key }, tx);
                    Count("grants");
                }
            }

            foreach (var u in Arr(root, "users"))
            {
                var id = S(u, "id")!;
                var email = S(u, "email");
                if (await Exists("core.app_user", "user_id", id)) continue;
                if (email is not null && await Exists("core.app_user", "email", email)) continue;
                var first = S(u, "firstName") ?? ""; var last = S(u, "lastName") ?? "";
                await q.Query("core.app_user").InsertAsync(new Dictionary<string, object?>
                {
                    ["user_id"] = id, ["username"] = S(u, "username") ?? email ?? id,
                    ["name_en"] = S(u, "nameEn") ?? $"{first} {last}".Trim(), ["name_ar"] = S(u, "nameAr") ?? $"{first} {last}".Trim(),
                    ["email"] = email, ["phone"] = S(u, "phone"), ["role_id"] = S(u, "roleId") ?? "rol_cashier",
                    ["employee_no"] = int.TryParse(S(u, "employeeId")?.TrimStart('E', 'M', 'P', '-'), out var no) ? no : null,
                    ["status"] = S(u, "status") ?? "active", ["is_external"] = false, ["base_salary_h"] = 0L,
                    ["created_at_utc"] = now, ["updated_at_utc"] = now, ["created_by"] = "seed",
                }, tx);
                foreach (var branch in Arr(u, "branchIds").Select(x => x!.GetValue<string>()))
                    if (await Exists("core.branch", "branch_id", branch))
                        await q.Query("core.user_branch").InsertAsync(new { user_id = id, branch_id = branch }, tx);
                Count("users");
            }

            foreach (var c in Arr(root, "categories"))
            {
                var id = S(c, "id")!;
                if (await Exists("core.category", "category_id", id)) continue;
                await q.Query("core.category").InsertAsync(new Dictionary<string, object?>
                {
                    ["category_id"] = id, ["name_ar"] = S(c, "nameAr") ?? "", ["name_en"] = S(c, "nameEn") ?? "",
                    ["applies_to"] = S(c, "appliesTo"), ["sort_order"] = I(c, "sortOrder"), ["is_active"] = S(c, "status") != "inactive",
                    ["details_json"] = c!.ToJsonString(), ["created_at_utc"] = now, ["updated_at_utc"] = now,
                }, tx);
                Count("categories");
            }

            foreach (var g in Arr(root, "printGroups"))
            {
                var id = S(g, "id")!;
                if (await Exists("core.print_group", "print_group_id", id)) continue;
                await q.Query("core.print_group").InsertAsync(new Dictionary<string, object?>
                {
                    ["print_group_id"] = id, ["name_ar"] = S(g, "nameAr") ?? "", ["name_en"] = S(g, "nameEn") ?? "",
                    ["ticket_title"] = S(g, "ticketTitle"), ["sort_order"] = I(g, "sortOrder"), ["is_active"] = S(g, "status") != "inactive",
                    ["created_at_utc"] = now, ["updated_at_utc"] = now,
                }, tx);
                Count("printGroups");
            }

            foreach (var item in Arr(root, "catalog"))
            {
                var id = S(item, "id")!;
                if (await Exists("core.catalog_item", "item_id", id)) continue;
                await q.Query("core.catalog_item").InsertAsync(PosApi.Features.Catalog.CatalogRow.From(item!.AsObject(), now), tx);
                Count("items");
            }
            return 0;
        }, ct);

        await log.WriteLineAsync(added.Count == 0
            ? "Seed: nothing to add (all setup data already present)."
            : "Seed added: " + string.Join(", ", added.Select(kv => $"{kv.Value} {kv.Key}")));
        return 0;
    }

    internal static string? S(JsonNode? n, string key) =>
        n?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    internal static int I(JsonNode? n, string key) =>
        n?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : 0;

    internal static IEnumerable<JsonNode?> Arr(JsonNode? n, string key) =>
        n?[key] is JsonArray a ? a : Enumerable.Empty<JsonNode?>();

    private static string Sensitivity(string key) =>
        key.StartsWith("finance") || key.StartsWith("restaurant.cash") || key.Contains("void") || key.Contains("refund")
        || key.Contains("return") || key.Contains("override_price") || key.StartsWith("zatca") || key.Contains("approve_discrepancy")
            ? "financial"
            : key.StartsWith("users") || key.StartsWith("payroll") || key.StartsWith("staff") ? "personal" : "general";
}
