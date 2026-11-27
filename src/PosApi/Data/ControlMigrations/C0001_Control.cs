using FluentMigrator;

namespace PosApi.Data.ControlMigrations;

/// <summary>
/// The control database (SaaS mode): the list of clients, where each client's database is, and the super admins.
/// Never holds a client's sales or customers; those stay in the client's own database.
/// </summary>
[Migration(1, "Control: clients and super admins")]
public sealed class C0001_Control : Migration
{
    public override void Up()
    {
        Create.Schema("control");
        Create.Table("tenant").InSchema("control")
            .WithColumn("tenant_id").AsInt64().PrimaryKey("pk_tenant").Identity()
            .WithColumn("slug").AsString(40).NotNullable().Unique("ux_tenant_slug")       // pos1 → pos1.wateeq.com
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("custom_host").AsString(200).Nullable().Unique("ux_tenant_host") // optional own domain
            .WithColumn("status").AsString(20).NotNullable()                              // setup | active | suspended
            .WithColumn("plan").AsString(40).Nullable()
            .WithColumn("db_host").AsString(200).NotNullable()
            .WithColumn("db_port").AsInt32().NotNullable()
            .WithColumn("db_name").AsString(100).NotNullable()
            .WithColumn("db_user").AsString(100).NotNullable()
            .WithColumn("db_password_enc").AsString(1000).NotNullable()
            .WithColumn("contact_name").AsString(200).Nullable()
            .WithColumn("contact_email").AsString(200).Nullable()
            .WithColumn("contact_phone").AsString(40).Nullable()
            .WithColumn("notes").AsString(2000).Nullable()
            .WithColumn("schema_version").AsInt64().Nullable()
            .WithColumn("migrated_at_utc").AsDateTime2().Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable();

        Create.Table("tenant_event").InSchema("control")
            .WithColumn("event_id").AsInt64().PrimaryKey("pk_tenant_event").Identity()
            .WithColumn("tenant_id").AsInt64().Nullable()
            .WithColumn("kind").AsString(40).NotNullable()          // created, migrated, suspended, owner_created, error …
            .WithColumn("detail").AsString(4000).Nullable()
            .WithColumn("actor").AsString(200).Nullable()
            .WithColumn("at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_tenant_event_tenant").OnTable("tenant_event").InSchema("control").OnColumn("tenant_id");

        Create.Table("admin_user").InSchema("control")
            .WithColumn("admin_id").AsInt64().PrimaryKey("pk_admin_user").Identity()
            .WithColumn("email").AsString(200).NotNullable().Unique("ux_admin_email")
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("password_hash").AsString(500).NotNullable()
            .WithColumn("password_salt").AsString(200).NotNullable()
            .WithColumn("last_login_at_utc").AsDateTime2().Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("admin_user").InSchema("control");
        Delete.Table("tenant_event").InSchema("control");
        Delete.Table("tenant").InSchema("control");
        Delete.Schema("control");
    }
}
