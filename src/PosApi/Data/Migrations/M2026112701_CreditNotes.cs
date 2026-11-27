using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>Returns (credit notes) on the server: numbered 500001+, refund counted in that day's cash, stock back.</summary>
[Migration(2026112701, "Credit notes")]
public sealed class M2026112701_CreditNotes : Migration
{
    public override void Up()
    {
        Create.Table("credit_note").InSchema("sales")
            .WithColumn("note_no").AsInt64().PrimaryKey("pk_credit_note")
            .WithColumn("sale_id").AsInt64().NotNullable().ForeignKey("fk_credit_note_sale", "sales", "sale", "sale_id")
            .WithColumn("session_id").AsInt64().Nullable()
            .WithColumn("branch_id").AsInt64().NotNullable()
            .WithColumn("refund_method").AsString(20).NotNullable()
            .WithColumn("subtotal_h").AsInt64().NotNullable()
            .WithColumn("tax_h").AsInt64().NotNullable()
            .WithColumn("total_h").AsInt64().NotNullable()
            .WithColumn("reason").AsString(40).NotNullable()
            .WithColumn("issued_by").AsString(100).Nullable()
            .WithColumn("idempotency_key").AsString(80).Nullable().Unique("ux_credit_note_idempotency")
            .WithColumn("document_json").AsString(int.MaxValue).NotNullable()
            .WithColumn("issued_at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_credit_note_sale").OnTable("credit_note").InSchema("sales").OnColumn("sale_id");
        Create.Index("ix_credit_note_session").OnTable("credit_note").InSchema("sales").OnColumn("session_id");

        /* The day a sale was cancelled in: cancelling yesterday's cash sale refunds cash from today's drawer. */
        Alter.Table("sale").InSchema("sales").AddColumn("void_session_id").AsInt64().Nullable();

        /* The front end's words for a returned sale. Same SQL on PostgreSQL and SQL Server. */
        Execute.Sql("ALTER TABLE sales.sale DROP CONSTRAINT ck_sale_status");
        Execute.Sql("ALTER TABLE sales.sale ADD CONSTRAINT ck_sale_status CHECK (status IN ('completed','voided','returned','partially_returned','refunded','partially_refunded'))");
    }

    public override void Down()
    {
        Delete.Column("void_session_id").FromTable("sale").InSchema("sales");
        Delete.Table("credit_note").InSchema("sales");
    }
}
