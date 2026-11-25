using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>
/// Catalog, restaurant business sessions and sales, on every engine.
///
/// Relational columns hold what is filtered, summed, joined or numbered (prices, totals, numbers, status, dates).
/// The rest of a catalog item (modifier groups, combo slots, recipe) and the full sale document are kept as JSON
/// text next to them, so the front end gets back exactly what it saved and nothing is lost in translation.
/// </summary>
[Migration(2026112501, "Catalog, restaurant sessions and sales")]
public sealed class M2026112501_CatalogSessionsSales : Migration
{
    private const int Max = int.MaxValue;

    public override void Up()
    {
        Create.Schema("sales");

        /* ------------------------------------------------------------ catalog */
        Create.Table("category").InSchema("core")
            .WithColumn("category_id").AsString(40).PrimaryKey("pk_category")
            .WithColumn("name_ar").AsString(200).NotNullable()
            .WithColumn("name_en").AsString(200).NotNullable()
            .WithColumn("applies_to").AsString(20).Nullable()
            .WithColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("details_json").AsString(Max).Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();

        Create.Table("print_group").InSchema("core")
            .WithColumn("print_group_id").AsString(40).PrimaryKey("pk_print_group")
            .WithColumn("name_ar").AsString(200).NotNullable()
            .WithColumn("name_en").AsString(200).NotNullable()
            .WithColumn("ticket_title").AsString(60).Nullable()
            .WithColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();

        Create.Table("catalog_item").InSchema("core")
            .WithColumn("item_id").AsString(40).PrimaryKey("pk_catalog_item")
            .WithColumn("kind").AsString(20).NotNullable()
            .WithColumn("name_ar").AsString(200).NotNullable()
            .WithColumn("name_en").AsString(200).NotNullable()
            .WithColumn("category_id").AsString(40).Nullable()
            .WithColumn("print_group_id").AsString(40).Nullable()
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
            .WithColumn("session_id").AsString(60).PrimaryKey("pk_restaurant_session")
            .WithColumn("session_no").AsInt64().NotNullable().Unique("uq_restaurant_session_no")
            .WithColumn("branch_id").AsString(40).NotNullable()
            .WithColumn("register_id").AsString(40).NotNullable()
            .WithColumn("business_date").AsDate().NotNullable()
            .WithColumn("status").AsString(20).NotNullable()
            /* "branch|register" while open, the session id once closed: unique on every engine, so a second open
               session for the same till is refused by the database itself. */
            .WithColumn("open_key").AsString(100).NotNullable().Unique("uq_restaurant_session_open")
            .WithColumn("opened_at_utc").AsDateTime2().NotNullable()
            .WithColumn("opened_by").AsString(200).NotNullable()
            .WithColumn("opened_by_id").AsString(60).Nullable()
            .WithColumn("opening_balance_h").AsInt64().NotNullable()
            .WithColumn("opening_notes").AsString(1000).Nullable()
            .WithColumn("closed_at_utc").AsDateTime2().Nullable()
            .WithColumn("closed_by").AsString(200).Nullable()
            .WithColumn("closed_by_id").AsString(60).Nullable()
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
            .WithColumn("movement_id").AsString(60).PrimaryKey("pk_session_movement")
            .WithColumn("session_id").AsString(60).NotNullable().ForeignKey("fk_movement_session", "sales", "restaurant_session", "session_id")
            .WithColumn("movement_type").AsString(20).NotNullable()
            .WithColumn("amount_h").AsInt64().NotNullable()
            .WithColumn("description").AsString(500).NotNullable()
            .WithColumn("user_name").AsString(200).NotNullable()
            .WithColumn("details_json").AsString(Max).Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable();

        Create.Table("session_audit").InSchema("sales")
            .WithColumn("session_audit_id").AsString(60).PrimaryKey("pk_session_audit")
            .WithColumn("session_id").AsString(60).NotNullable().ForeignKey("fk_session_audit_session", "sales", "restaurant_session", "session_id")
            .WithColumn("action").AsString(40).NotNullable()
            .WithColumn("description").AsString(1000).NotNullable()
            .WithColumn("old_value").AsString(1000).Nullable()
            .WithColumn("new_value").AsString(1000).Nullable()
            .WithColumn("user_name").AsString(200).NotNullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable();

        /* ---------------------------------------------------------------- sales */
        Create.Table("sale").InSchema("sales")
            .WithColumn("sale_id").AsString(60).PrimaryKey("pk_sale")
            .WithColumn("invoice_no").AsInt64().NotNullable().Unique("uq_sale_invoice_no")
            .WithColumn("order_no").AsInt32().NotNullable()
            .WithColumn("session_id").AsString(60).NotNullable().ForeignKey("fk_sale_session", "sales", "restaurant_session", "session_id")
            .WithColumn("branch_id").AsString(40).NotNullable()
            .WithColumn("status").AsString(20).NotNullable()
            .WithColumn("order_type").AsString(30).Nullable()
            .WithColumn("customer_id").AsString(60).Nullable()
            .WithColumn("cashier_name").AsString(200).NotNullable()
            .WithColumn("created_by_id").AsString(60).Nullable()
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
            .WithColumn("sale_id").AsString(60).NotNullable().PrimaryKey("pk_sale_line")
                .ForeignKey("fk_sale_line_sale", "sales", "sale", "sale_id")
            .WithColumn("line_no").AsInt32().NotNullable().PrimaryKey("pk_sale_line")
            .WithColumn("item_id").AsString(40).NotNullable()
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
            .WithColumn("sale_id").AsString(60).NotNullable().PrimaryKey("pk_sale_payment")
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

    public override void Down()
    {
        foreach (var t in new[] { "zatca_chain", "sale_payment", "sale_line", "sale", "session_audit", "session_movement", "restaurant_session" })
            Delete.Table(t).InSchema("sales");
        foreach (var t in new[] { "catalog_item", "print_group", "category" }) Delete.Table(t).InSchema("core");
        Delete.Schema("sales");
    }
}
