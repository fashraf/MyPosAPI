using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>What happened to an order besides the sale and the kitchen ticket themselves (prints, for now).</summary>
[Migration(2026112605, "Order events")]
public sealed class M2026112605_OrderEvents : Migration
{
    public override void Up()
    {
        Create.Table("order_event").InSchema("sales")
            .WithColumn("event_id").AsInt64().PrimaryKey("pk_order_event").Identity()
            .WithColumn("invoice_no").AsInt64().NotNullable()
            .WithColumn("kind").AsString(30).NotNullable()          // print_receipt, print_kitchen, ...
            .WithColumn("detail").AsString(200).Nullable()          // printer name
            .WithColumn("actor").AsString(100).Nullable()
            .WithColumn("at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_order_event_invoice").OnTable("order_event").InSchema("sales").OnColumn("invoice_no");
    }

    public override void Down() => Delete.Table("order_event").InSchema("sales");
}
