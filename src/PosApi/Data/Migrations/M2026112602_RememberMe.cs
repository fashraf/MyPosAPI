using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>"Remember me" sessions: kept for months and renewed on use, ended only by signing out.</summary>
[Migration(2026112602, "Remember me")]
public sealed class M2026112602_RememberMe : Migration
{
    public override void Up() =>
        Alter.Table("refresh_token").InSchema("core").AddColumn("remember_me").AsBoolean().NotNullable().WithDefaultValue(false);

    public override void Down() => Delete.Column("remember_me").FromTable("refresh_token").InSchema("core");
}
