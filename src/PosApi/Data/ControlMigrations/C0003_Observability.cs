using FluentMigrator;

namespace PosApi.Data.ControlMigrations;

/// <summary>
/// One place for every error (front end, API, SQL, network) from every client and from Ayn itself, the front-end
/// audit (screens opened, actions taken), and a GUID per client (its database is named after it).
/// </summary>
[Migration(3, "Control: error log, front-end events, client GUID")]
public sealed class C0003_Observability : Migration
{
    public override void Up()
    {
        Create.Table("error_log").InSchema("control")
            .WithColumn("error_id").AsInt64().PrimaryKey("pk_error_log").Identity()
            .WithColumn("tenant_id").AsInt64().Nullable()
            .WithColumn("source").AsString(20).NotNullable()          // frontend | backend | sql | network
            .WithColumn("app").AsString(20).NotNullable()             // pos | ayn | api
            .WithColumn("message").AsString(600).NotNullable()
            .WithColumn("detail").AsString(int.MaxValue).Nullable()
            .WithColumn("url").AsString(400).Nullable()
            .WithColumn("method").AsString(10).Nullable()
            .WithColumn("status_code").AsInt32().Nullable()
            .WithColumn("user_name").AsString(160).Nullable()
            .WithColumn("user_agent").AsString(300).Nullable()
            .WithColumn("ip").AsString(60).Nullable()
            .WithColumn("fingerprint").AsString(64).NotNullable()
            .WithColumn("occurrences").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("first_at_utc").AsDateTime2().NotNullable()
            .WithColumn("last_at_utc").AsDateTime2().NotNullable()
            .WithColumn("resolved").AsBoolean().NotNullable().WithDefaultValue(false);
        Create.Index("ix_error_last").OnTable("error_log").InSchema("control").OnColumn("last_at_utc").Descending();
        Create.Index("ix_error_fp").OnTable("error_log").InSchema("control").OnColumn("fingerprint").Ascending();

        Create.Table("ui_event").InSchema("control")
            .WithColumn("event_id").AsInt64().PrimaryKey("pk_ui_event").Identity()
            .WithColumn("tenant_id").AsInt64().Nullable()
            .WithColumn("app").AsString(20).NotNullable()
            .WithColumn("actor").AsString(160).Nullable()
            .WithColumn("kind").AsString(20).NotNullable()            // view | action | slow
            .WithColumn("page").AsString(200).Nullable()
            .WithColumn("label").AsString(300).Nullable()
            .WithColumn("duration_ms").AsInt32().Nullable()
            .WithColumn("at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_ui_event_at").OnTable("ui_event").InSchema("control").OnColumn("at_utc").Descending();

        Alter.Table("tenant").InSchema("control").AddColumn("client_guid").AsString(40).Nullable();
    }

    public override void Down()
    {
        Delete.Column("client_guid").FromTable("tenant").InSchema("control");
        Delete.Table("ui_event").InSchema("control");
        Delete.Table("error_log").InSchema("control");
    }
}
