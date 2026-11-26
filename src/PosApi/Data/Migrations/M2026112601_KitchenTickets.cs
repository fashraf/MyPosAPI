using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>Kitchen tickets on the server, so every device (kitchen screen, collection, phone) sees the same orders.</summary>
[Migration(2026112601, "Kitchen tickets")]
public sealed class M2026112601_KitchenTickets : Migration
{
    public override void Up()
    {
        Create.Table("kitchen_ticket").InSchema("sales")
            .WithColumn("invoice_no").AsInt64().PrimaryKey("pk_kitchen_ticket")      // one ticket per order (its invoice)
            .WithColumn("status").AsString(20).NotNullable()
            .WithColumn("document_json").AsString(int.MaxValue).NotNullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_kitchen_ticket_updated").OnTable("kitchen_ticket").InSchema("sales").OnColumn("updated_at_utc").Descending();
    }

    public override void Down() => Delete.Table("kitchen_ticket").InSchema("sales");
}
