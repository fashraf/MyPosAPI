using FluentMigrator;

namespace PosApi.Data.ControlMigrations;

/// <summary>Server load sampled every minute (for gauges and today / yesterday / last-week comparisons) and payments received.</summary>
[Migration(4, "Control: server load samples, payments")]
public sealed class C0004_LoadAndPayments : Migration
{
    public override void Up()
    {
        Create.Table("server_sample").InSchema("control")
            .WithColumn("sample_id").AsInt64().PrimaryKey("pk_server_sample").Identity()
            .WithColumn("at_utc").AsDateTime2().NotNullable()
            .WithColumn("cpu_pct").AsDecimal(6, 2).NotNullable()
            .WithColumn("memory_pct").AsDecimal(6, 2).NotNullable()
            .WithColumn("process_mb").AsInt32().NotNullable()
            .WithColumn("disk_free_pct").AsDecimal(6, 2).NotNullable()
            .WithColumn("db_connections").AsInt32().NotNullable()
            .WithColumn("requests").AsInt32().NotNullable()
            .WithColumn("errors").AsInt32().NotNullable();
        Create.Index("ix_server_sample_at").OnTable("server_sample").InSchema("control").OnColumn("at_utc").Descending();

        Create.Table("payment").InSchema("control")
            .WithColumn("payment_id").AsInt64().PrimaryKey("pk_payment").Identity()
            .WithColumn("tenant_id").AsInt64().NotNullable()
            .WithColumn("amount_h").AsInt64().NotNullable()
            .WithColumn("method").AsString(40).NotNullable()
            .WithColumn("reference").AsString(120).Nullable()
            .WithColumn("period").AsString(60).Nullable()
            .WithColumn("note").AsString(400).Nullable()
            .WithColumn("paid_on").AsDate().NotNullable()
            .WithColumn("recorded_by").AsString(160).Nullable()
            .WithColumn("recorded_at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_payment_tenant").OnTable("payment").InSchema("control").OnColumn("tenant_id").Ascending();
    }

    public override void Down()
    {
        Delete.Table("payment").InSchema("control");
        Delete.Table("server_sample").InSchema("control");
    }
}
