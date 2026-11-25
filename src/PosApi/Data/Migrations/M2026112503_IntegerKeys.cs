using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>
/// Integer primary keys: branch 1, user 1, item 1, category 1, session 1001, sale = its invoice number … instead of text
/// ids such as "brn_001". Text stays only where the key is a fixed code the program refers to (role and permission
/// keys, number-series keys).
///
/// The tables involved held staging test data only (production was never deployed), so they are recreated rather than
/// converted, and the number series restart: invoices from 1, the order number from 1 every business day. The seed
/// (--seed) loads the setup data again on the same deploy.
/// </summary>
[Migration(2026112503, "Integer primary keys")]
public sealed class M2026112503_IntegerKeys : Migration
{
    private const int Max = int.MaxValue;

    public override void Up()
    {
        foreach (var t in new[] { "sale_payment", "sale_line", "sale", "session_audit", "session_movement", "restaurant_session", "zatca_chain" })
            Delete.Table(t).InSchema("sales");
        foreach (var t in new[] { "catalog_item", "category", "print_group", "refresh_token", "user_permission_override",
                                  "user_branch", "branch_settings", "app_user", "branch" })
            Delete.Table(t).InSchema("core");
        Execute.Sql("DELETE FROM number_series WHERE series_key IN ('invoice','session','branch','employee','sale')");

        Create.Table("branch").InSchema("core")
            .WithColumn("branch_id").AsInt64().PrimaryKey("pk_branch")
            .WithColumn("code").AsString(20).NotNullable().Unique("uq_branch_code")
            .WithColumn("name_ar").AsString(160).NotNullable()
            .WithColumn("name_en").AsString(160).NotNullable()
            .WithColumn("phone").AsString(30).Nullable()
            .WithColumn("address_ar").AsString(300).Nullable()
            .WithColumn("address_en").AsString(300).Nullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Table("branch_settings").InSchema("core")
            .WithColumn("branch_id").AsInt64().PrimaryKey("pk_branch_settings")
                .ForeignKey("fk_branch_settings_branch", "core", "branch", "branch_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("default_customer_mode").AsString(20).NotNullable().WithDefaultValue("walkin")
            .WithColumn("served_by_requirement").AsString(20).NotNullable().WithDefaultValue("optional")
            .WithColumn("allow_negative_stock").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("default_bill_id").AsString(40).Nullable()
            .WithColumn("auto_print_customer").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("auto_print_groups").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("vat_rate_percent").AsDecimal(5, 2).NotNullable().WithDefaultValue(15.00m)
            .WithColumn("prices_include_vat").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("enabled_order_types").AsString(200).NotNullable().WithDefaultValue("dine_in,takeaway,delivery")
            .WithColumn("default_order_type").AsString(20).NotNullable().WithDefaultValue("takeaway")
            .WithColumn("kitchen_routing_json").AsString(400).Nullable()
            .WithColumn("receipt_timing_json").AsString(400).Nullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_by").AsString(120).Nullable();

        Create.Table("app_user").InSchema("core")
            .WithColumn("user_id").AsInt64().PrimaryKey("pk_app_user")
            .WithColumn("username").AsString(60).NotNullable().Unique("uq_app_user_username")
            .WithColumn("name_ar").AsString(160).NotNullable()
            .WithColumn("name_en").AsString(160).NotNullable()
            .WithColumn("email").AsString(160).Nullable()
            .WithColumn("phone").AsString(30).Nullable()
            .WithColumn("role_id").AsString(40).NotNullable().ForeignKey("fk_app_user_role", "core", "role", "role_id")
            .WithColumn("employee_no").AsInt32().Nullable()
            .WithColumn("password_hash").AsString(400).Nullable()
            .WithColumn("password_salt").AsString(200).Nullable()
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("active")
            .WithColumn("is_external").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("access_expires_on").AsDate().Nullable()
            .WithColumn("base_salary_h").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("last_login_at_utc").AsDateTime2().Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(120).Nullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_by").AsString(120).Nullable();

        Create.Table("user_branch").InSchema("core")
            .WithColumn("user_id").AsInt64().PrimaryKey("pk_user_branch")
                .ForeignKey("fk_user_branch_user", "core", "app_user", "user_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("branch_id").AsInt64().PrimaryKey("pk_user_branch")
                .ForeignKey("fk_user_branch_branch", "core", "branch", "branch_id");

        Create.Table("user_permission_override").InSchema("core")
            .WithColumn("user_id").AsInt64().PrimaryKey("pk_user_permission_override")
                .ForeignKey("fk_user_override_user", "core", "app_user", "user_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("permission_key").AsString(60).PrimaryKey("pk_user_permission_override")
                .ForeignKey("fk_user_override_permission", "core", "permission", "permission_key")
            .WithColumn("effect").AsString(10).NotNullable();

        Create.Table("refresh_token").InSchema("core")
            .WithColumn("token_hash").AsString(200).PrimaryKey("pk_refresh_token")
            .WithColumn("user_id").AsInt64().NotNullable()
                .ForeignKey("fk_refresh_token_user", "core", "app_user", "user_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("issued_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("expires_at_utc").AsDateTime2().NotNullable()
            .WithColumn("revoked_at_utc").AsDateTime2().Nullable()
            .WithColumn("user_agent").AsString(300).Nullable()
            .WithColumn("ip_address").AsString(60).Nullable();
        Create.Index("ix_app_user_email").OnTable("app_user").InSchema("core").OnColumn("email");
        Create.Index("ix_app_user_phone").OnTable("app_user").InSchema("core").OnColumn("phone");
        Create.Index("ix_refresh_token_user").OnTable("refresh_token").InSchema("core").OnColumn("user_id");

        foreach (var (table, name, rule) in new[]
        {
            ("core.branch_settings", "ck_bs_mode", "default_customer_mode IN ('walkin','customer')"),
            ("core.branch_settings", "ck_bs_served_by", "served_by_requirement IN ('off','optional','required')"),
            ("core.branch_settings", "ck_bs_vat", "vat_rate_percent >= 0 AND vat_rate_percent <= 100"),
            ("core.app_user", "ck_app_user_status", "status IN ('active','suspended','expired')"),
            ("core.app_user", "ck_app_user_salary", "base_salary_h >= 0"),
            ("core.user_permission_override", "ck_user_override_effect", "effect IN ('grant','deny')"),
        })
        {
            Execute.Sql($"ALTER TABLE {table} ADD CONSTRAINT {name} CHECK ({rule})");
        }

        Create.Table("category").InSchema("core")
            .WithColumn("category_id").AsInt64().PrimaryKey("pk_category")
            .WithColumn("name_ar").AsString(200).NotNullable()
            .WithColumn("name_en").AsString(200).NotNullable()
            .WithColumn("applies_to").AsString(20).Nullable()
            .WithColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("details_json").AsString(Max).Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();

        Create.Table("print_group").InSchema("core")
            .WithColumn("print_group_id").AsInt64().PrimaryKey("pk_print_group")
            .WithColumn("name_ar").AsString(200).NotNullable()
            .WithColumn("name_en").AsString(200).NotNullable()
            .WithColumn("ticket_title").AsString(60).Nullable()
            .WithColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();

        Create.Table("catalog_item").InSchema("core")
            .WithColumn("item_id").AsInt64().PrimaryKey("pk_catalog_item")
            .WithColumn("kind").AsString(20).NotNullable()
            .WithColumn("name_ar").AsString(200).NotNullable()
            .WithColumn("name_en").AsString(200).NotNullable()
            .WithColumn("category_id").AsInt64().Nullable()
            .WithColumn("print_group_id").AsInt64().Nullable()
            .WithColumn("sku").AsString(60).Nullable()
            .WithColumn("barcode").AsString(60).Nullable()
            .WithColumn("price_h").AsInt64().NotNullable()
            .WithColumn("cost_h").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("tax_rate_id").AsString(40).Nullable()
            .WithColumn("show_on_pos").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("requires_preparation").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("track_inventory").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("stock_quantity").AsDecimal(18, 3).NotNullable().WithDefaultValue(0)
            .WithColumn("min_stock_level").AsDecimal(18, 3).NotNullable().WithDefaultValue(0)
            .WithColumn("unit").AsString(20).Nullable()
            .WithColumn("is_combo").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("active")
            .WithColumn("details_json").AsString(Max).Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_catalog_item_category").OnTable("catalog_item").InSchema("core").OnColumn("category_id");

        /* ------------------------------------------------- restaurant sessions */
        Create.Table("restaurant_session").InSchema("sales")
            .WithColumn("session_id").AsInt64().PrimaryKey("pk_restaurant_session")
            .WithColumn("session_no").AsInt64().NotNullable().Unique("uq_restaurant_session_no")
            .WithColumn("branch_id").AsInt64().NotNullable()
            .WithColumn("register_id").AsString(40).NotNullable()
            .WithColumn("business_date").AsDate().NotNullable()
            .WithColumn("status").AsString(20).NotNullable()
            /* "branch|register" while open, the session id once closed: unique on every engine, so a second open
               session for the same till is refused by the database itself. */
            .WithColumn("open_key").AsString(100).NotNullable().Unique("uq_restaurant_session_open")
            .WithColumn("opened_at_utc").AsDateTime2().NotNullable()
            .WithColumn("opened_by").AsString(200).NotNullable()
            .WithColumn("opened_by_id").AsInt64().Nullable()
            .WithColumn("opening_balance_h").AsInt64().NotNullable()
            .WithColumn("opening_notes").AsString(1000).Nullable()
            .WithColumn("closed_at_utc").AsDateTime2().Nullable()
            .WithColumn("closed_by").AsString(200).Nullable()
            .WithColumn("closed_by_id").AsInt64().Nullable()
            .WithColumn("expected_cash_h").AsInt64().Nullable()
            .WithColumn("actual_closing_h").AsInt64().Nullable()
            .WithColumn("discrepancy_h").AsInt64().Nullable()
            .WithColumn("discrepancy_reason").AsString(1000).Nullable()
            .WithColumn("closing_notes").AsString(1000).Nullable()
            .WithColumn("last_order_no").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("reopen_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("details_json").AsString(Max).Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_restaurant_session_branch").OnTable("restaurant_session").InSchema("sales")
            .OnColumn("branch_id").Ascending().OnColumn("opened_at_utc").Descending();

        Create.Table("session_movement").InSchema("sales")
            .WithColumn("movement_id").AsInt64().PrimaryKey("pk_session_movement")
            .WithColumn("session_id").AsInt64().NotNullable().ForeignKey("fk_movement_session", "sales", "restaurant_session", "session_id")
            .WithColumn("movement_type").AsString(20).NotNullable()
            .WithColumn("amount_h").AsInt64().NotNullable()
            .WithColumn("description").AsString(500).NotNullable()
            .WithColumn("user_name").AsString(200).NotNullable()
            .WithColumn("details_json").AsString(Max).Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable();

        Create.Table("session_audit").InSchema("sales")
            .WithColumn("session_audit_id").AsInt64().PrimaryKey("pk_session_audit")
            .WithColumn("session_id").AsInt64().NotNullable().ForeignKey("fk_session_audit_session", "sales", "restaurant_session", "session_id")
            .WithColumn("action").AsString(40).NotNullable()
            .WithColumn("description").AsString(1000).NotNullable()
            .WithColumn("old_value").AsString(1000).Nullable()
            .WithColumn("new_value").AsString(1000).Nullable()
            .WithColumn("user_name").AsString(200).NotNullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable();

        /* ---------------------------------------------------------------- sales */
        Create.Table("sale").InSchema("sales")
            .WithColumn("sale_id").AsInt64().PrimaryKey("pk_sale")
            .WithColumn("invoice_no").AsInt64().NotNullable().Unique("uq_sale_invoice_no")
            .WithColumn("order_no").AsInt32().NotNullable()
            .WithColumn("session_id").AsInt64().NotNullable().ForeignKey("fk_sale_session", "sales", "restaurant_session", "session_id")
            .WithColumn("branch_id").AsInt64().NotNullable()
            .WithColumn("status").AsString(20).NotNullable()
            .WithColumn("order_type").AsString(30).Nullable()
            .WithColumn("customer_id").AsString(60).Nullable()
            .WithColumn("cashier_name").AsString(200).NotNullable()
            .WithColumn("created_by_id").AsInt64().Nullable()
            .WithColumn("item_count").AsDecimal(18, 3).NotNullable()
            .WithColumn("subtotal_h").AsInt64().NotNullable()
            .WithColumn("discount_h").AsInt64().NotNullable()
            .WithColumn("tax_h").AsInt64().NotNullable()
            .WithColumn("total_h").AsInt64().NotNullable()
            .WithColumn("tendered_h").AsInt64().Nullable()
            .WithColumn("change_h").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("idempotency_key").AsString(100).Nullable()
            .WithColumn("zatca_uuid").AsString(60).NotNullable()
            .WithColumn("zatca_icv").AsInt64().NotNullable()
            .WithColumn("zatca_pih").AsString(100).NotNullable()
            .WithColumn("zatca_hash").AsString(100).NotNullable()
            .WithColumn("zatca_qr").AsString(2000).NotNullable()
            .WithColumn("void_reason").AsString(500).Nullable()
            .WithColumn("voided_at_utc").AsDateTime2().Nullable()
            .WithColumn("voided_by").AsString(200).Nullable()
            .WithColumn("sold_at_utc").AsDateTime2().NotNullable()
            .WithColumn("document_json").AsString(Max).NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_sale_session").OnTable("sale").InSchema("sales").OnColumn("session_id");
        Create.Index("ix_sale_branch_time").OnTable("sale").InSchema("sales")
            .OnColumn("branch_id").Ascending().OnColumn("sold_at_utc").Descending();
        Create.Index("ix_sale_idempotency").OnTable("sale").InSchema("sales").OnColumn("idempotency_key");

        Create.Table("sale_line").InSchema("sales")
            .WithColumn("sale_id").AsInt64().NotNullable().PrimaryKey("pk_sale_line")
                .ForeignKey("fk_sale_line_sale", "sales", "sale", "sale_id")
            .WithColumn("line_no").AsInt32().NotNullable().PrimaryKey("pk_sale_line")
            .WithColumn("item_id").AsInt64().NotNullable()
            .WithColumn("name_ar").AsString(300).NotNullable()
            .WithColumn("name_en").AsString(300).NotNullable()
            .WithColumn("quantity").AsDecimal(18, 3).NotNullable()
            .WithColumn("unit_price_h").AsInt64().NotNullable()
            .WithColumn("unit_cost_h").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("discount_h").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("line_total_h").AsInt64().NotNullable()
            .WithColumn("options_text").AsString(1000).Nullable()
            .WithColumn("details_json").AsString(Max).Nullable();

        Create.Table("sale_payment").InSchema("sales")
            .WithColumn("sale_id").AsInt64().NotNullable().PrimaryKey("pk_sale_payment")
                .ForeignKey("fk_sale_payment_sale", "sales", "sale", "sale_id")
            .WithColumn("payment_no").AsInt32().NotNullable().PrimaryKey("pk_sale_payment")
            .WithColumn("method").AsString(30).NotNullable()
            .WithColumn("amount_h").AsInt64().NotNullable()
            .WithColumn("reference").AsString(100).Nullable();

        /* One ZATCA chain per EGS unit (one per branch here): invoice counter and the hash of the last invoice. */
        Create.Table("zatca_chain").InSchema("sales")
            .WithColumn("egs_unit_id").AsString(60).PrimaryKey("pk_zatca_chain")
            .WithColumn("icv").AsInt64().NotNullable()
            .WithColumn("last_hash").AsString(100).NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();

        /* ------------------------------------------- portable CHECK constraints */
        foreach (var (table, name, rule) in new[]
        {
            ("sales.restaurant_session", "ck_session_status", "status IN ('open','closed')"),
            ("sales.restaurant_session", "ck_session_opening", "opening_balance_h >= 0"),
            ("sales.sale", "ck_sale_status", "status IN ('completed','voided','refunded','partially_refunded')"),
            ("sales.sale", "ck_sale_totals", "total_h >= 0 AND tax_h >= 0 AND discount_h >= 0"),
            ("sales.sale_line", "ck_line_quantity", "quantity > 0"),
            ("core.catalog_item", "ck_item_kind", "kind IN ('product','service')"),
            ("core.catalog_item", "ck_item_price", "price_h >= 0 AND cost_h >= 0"),
        })
        {
            Execute.Sql($"ALTER TABLE {table} ADD CONSTRAINT {name} CHECK ({rule})");
        }

    }

    /// <summary>Not reversible: the text-keyed tables held test data only.</summary>
    public override void Down() { }
}
