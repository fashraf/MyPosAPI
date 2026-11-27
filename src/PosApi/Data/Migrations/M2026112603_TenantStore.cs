using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>
/// Server copy of every module that does not have its own tables yet (discounts, inventory, expenses, payroll,
/// settings, users list, printers, cash, ledger…). One row per module; every device reads and writes the same row,
/// so nothing lives only in one browser any more. Modules move to their own tables one by one later.
/// </summary>
[Migration(2026112603, "Tenant store")]
public sealed class M2026112603_TenantStore : Migration
{
    public override void Up()
    {
        Create.Table("tenant_store").InSchema("core")
            .WithColumn("slice_name").AsString(40).PrimaryKey("pk_tenant_store")
            .WithColumn("revision").AsInt64().NotNullable()
            .WithColumn("data_json").AsString(int.MaxValue).NotNullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_by").AsString(64).Nullable();
    }

    public override void Down() => Delete.Table("tenant_store").InSchema("core");
}
