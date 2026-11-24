using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>
/// Reference data every installation needs: the permission catalogue and the six built-in roles with their grants.
/// (Same content as the SQL Server seed plus later additions: audit, devices, branches, user overrides, …)
/// </summary>
[Migration(2026112403, "Access reference data: permissions and built-in roles")]
public sealed class M2026112403_AccessSeed : Migration
{
    // key, module, action, sensitivity
    internal static readonly (string Key, string Module, string Action, string Sensitivity)[] Permissions =
    [
        ("dashboard.view", "dashboard", "view", "general"),
        ("pos.view", "pos", "view", "general"), ("pos.create_sale", "pos", "create_sale", "general"),
        ("pos.apply_discount", "pos", "apply_discount", "general"), ("pos.open_drawer", "pos", "open_drawer", "financial"),
        ("sales.view", "sales", "view", "general"), ("sales.view_details", "sales", "view_details", "general"),
        ("sales.refund", "sales", "refund", "financial"), ("sales.void", "sales", "void", "financial"),
        ("products.view", "products", "view", "general"), ("products.create", "products", "create", "general"),
        ("products.edit", "products", "edit", "general"), ("products.delete", "products", "delete", "general"),
        ("catalog.activate", "products", "activate", "general"),
        ("inventory.view", "inventory", "view", "general"), ("inventory.adjust", "inventory", "adjust", "general"),
        ("inventory.restock", "inventory", "restock", "general"), ("inventory.purchase_entry", "inventory", "purchase_entry", "financial"),
        ("vendors.view", "vendors", "view", "general"), ("vendors.create", "vendors", "create", "general"),
        ("vendors.edit", "vendors", "edit", "general"), ("vendors.pay", "vendors", "pay", "financial"),
        ("customers.view", "customers", "view", "general"), ("customers.create", "customers", "create", "general"),
        ("customers.edit", "customers", "edit", "general"), ("customers.credit", "customers", "credit", "financial"),
        ("discounts.view", "discounts", "view", "general"), ("discounts.create", "discounts", "create", "general"),
        ("discounts.edit", "discounts", "edit", "general"), ("discounts.approve", "discounts", "approve", "financial"),
        ("kitchen.view", "kitchen", "view", "general"), ("kitchen.update", "kitchen", "update", "general"),
        ("users.view", "users", "view", "personal"), ("users.create", "users", "create", "personal"),
        ("users.edit", "users", "edit", "personal"), ("users.delete", "users", "delete", "personal"),
        ("users.assign_branches", "users", "assign_branches", "general"), ("users.override", "users", "override", "general"),
        ("roles.view", "roles", "view", "personal"), ("roles.create", "roles", "create", "personal"), ("roles.edit", "roles", "edit", "personal"),
        ("finance.view", "finance", "view", "financial"), ("finance.expenses", "finance", "expenses", "financial"),
        ("finance.ledger", "finance", "ledger", "financial"), ("finance.reverse", "finance", "reverse", "financial"),
        ("finance.payroll", "finance", "payroll", "financial"), ("finance.commission", "finance", "commission", "financial"),
        ("finance.commission_rules", "finance", "commission_rules", "general"),
        ("reports.view", "reports", "view", "financial"),
        ("settings.view", "settings", "view", "general"), ("settings.edit", "settings", "edit", "general"),
        ("bill_builder.view", "bill_builder", "view", "general"), ("bill_builder.edit", "bill_builder", "edit", "general"),
        ("company.view", "company", "view", "general"), ("company.edit", "company", "edit", "general"),
        ("branches.create", "branches", "create", "general"), ("branches.edit", "branches", "edit", "general"),
        ("devices.view", "devices", "view", "general"), ("devices.manage", "devices", "manage", "general"),
        ("devices.command", "devices", "command", "general"), ("devices.discover", "devices", "discover", "general"),
        ("audit.view", "audit", "view", "personal"), ("audit.performance", "audit", "performance", "general"),
    ];

    internal static readonly (string Id, string NameAr, string NameEn, string DescAr, string DescEn, bool External)[] Roles =
    [
        ("rol_owner", "مالك", "Owner", "صلاحية كاملة على النظام", "Full access to everything", false),
        ("rol_manager", "مدير", "Manager", "إدارة الفرع والعمليات اليومية", "Runs the branch day to day", false),
        ("rol_cashier", "كاشير", "Cashier", "البيع وخدمة العملاء", "Sells and serves customers", false),
        ("rol_accountant", "محاسب", "Accountant", "المالية والتقارير", "Finance and reporting", false),
        ("rol_inventory", "أمين مستودع", "Inventory manager", "المخزون والموردون", "Stock and suppliers", false),
        ("rol_maintenance", "صيانة خارجية", "External support", "وصول مؤقت للصيانة", "Temporary maintenance access", true),
    ];

    /// <summary>Which permissions each built-in role gets (same rules as the original seed and its later additions).</summary>
    internal static IEnumerable<(string Role, string Key)> Grants()
    {
        var all = Permissions.Select(p => p.Key).ToArray();
        var managerExcluded = new HashSet<string>
        {
            "users.delete", "roles.create", "roles.edit", "finance.reverse", "audit.view", "users.assign_branches",
            "users.override", "finance.commission_rules", "branches.create", "branches.edit", "devices.manage",
        };
        string[] cashier = ["dashboard.view", "pos.view", "pos.create_sale", "pos.apply_discount", "sales.view", "sales.view_details",
                            "products.view", "customers.view", "customers.create", "discounts.view", "kitchen.view", "kitchen.update"];
        string[] accountant = ["dashboard.view", "sales.view", "sales.view_details", "products.view", "inventory.view", "vendors.view",
                               "vendors.pay", "customers.view", "customers.credit", "finance.view", "finance.expenses", "finance.ledger",
                               "finance.payroll", "finance.commission", "reports.view"];
        string[] inventory = ["dashboard.view", "products.view", "products.create", "products.edit", "inventory.view", "inventory.adjust",
                              "inventory.restock", "inventory.purchase_entry", "vendors.view", "vendors.create", "vendors.edit"];
        var maintenance = Permissions.Where(p => p.Sensitivity == "general"
            && p.Module is "dashboard" or "products" or "inventory" or "settings").Select(p => p.Key);

        foreach (var k in all) yield return ("rol_owner", k);
        foreach (var k in all.Where(k => !managerExcluded.Contains(k))) yield return ("rol_manager", k);
        foreach (var k in cashier) yield return ("rol_cashier", k);
        foreach (var k in accountant) yield return ("rol_accountant", k);
        foreach (var k in inventory) yield return ("rol_inventory", k);
        foreach (var k in maintenance) yield return ("rol_maintenance", k);
    }

    public override void Up()
    {
        foreach (var p in Permissions)
            Insert.IntoTable("permission").InSchema("core")
                .Row(new { permission_key = p.Key, module = p.Module, action = p.Action, sensitivity = p.Sensitivity });
        foreach (var r in Roles)
            Insert.IntoTable("role").InSchema("core").Row(new
            {
                role_id = r.Id, name_ar = r.NameAr, name_en = r.NameEn, description_ar = r.DescAr, description_en = r.DescEn,
                is_built_in = true, is_external = r.External,
            });
        foreach (var (role, key) in Grants())
            Insert.IntoTable("role_permission").InSchema("core").Row(new { role_id = role, permission_key = key });
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM core.role_permission");
        Execute.Sql("DELETE FROM core.role");
        Execute.Sql("DELETE FROM core.permission");
    }
}
