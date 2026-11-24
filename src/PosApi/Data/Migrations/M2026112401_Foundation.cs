using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>
/// First engine-neutral migration. Naming convention for every table and column: lower_snake_case
/// (works unquoted on PostgreSQL, SQL Server, MySQL and SQLite).
/// </summary>
[Migration(2026112401, "Foundation: business number counters")]
public sealed class M2026112401_Foundation : Migration
{
    public override void Up()
    {
        Create.Table("number_series")
            .WithColumn("series_key").AsString(30).NotNullable().PrimaryKey("pk_number_series")
            .WithColumn("scope_id").AsString(40).NotNullable().PrimaryKey("pk_number_series")
            .WithColumn("last_value").AsInt64().NotNullable();
    }

    public override void Down() => Delete.Table("number_series");
}
