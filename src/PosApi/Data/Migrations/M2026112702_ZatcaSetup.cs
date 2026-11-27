using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>ZATCA (Fatoora) phase 2 onboarding: seller details, the device (EGS unit), its key and certificates.</summary>
[Migration(2026112702, "ZATCA setup")]
public sealed class M2026112702_ZatcaSetup : Migration
{
    public override void Up()
    {
        Create.Table("zatca_setup").InSchema("core")
            .WithColumn("setup_id").AsInt32().PrimaryKey("pk_zatca_setup")          // one per business (1)
            .WithColumn("environment").AsString(20).NotNullable()                   // sandbox | simulation | production
            .WithColumn("details_json").AsString(int.MaxValue).NotNullable()        // seller, address, device
            .WithColumn("status").AsString(30).NotNullable()                        // draft | csr_ready | compliance | live
            .WithColumn("private_key_enc").AsString(int.MaxValue).Nullable()        // encrypted
            .WithColumn("csr_pem").AsString(int.MaxValue).Nullable()
            .WithColumn("compliance_json_enc").AsString(int.MaxValue).Nullable()    // encrypted {requestId, token, secret}
            .WithColumn("production_json_enc").AsString(int.MaxValue).Nullable()    // encrypted {token, secret}
            .WithColumn("last_error").AsString(2000).Nullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable()
            .WithColumn("updated_by").AsString(100).Nullable();
    }

    public override void Down() => Delete.Table("zatca_setup").InSchema("core");
}
