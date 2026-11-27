using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>The SQL statements each request ran, for the API performance page (text and parameter names, no values).</summary>
[Migration(2026112502, "Query timing")]
public sealed class M2026112502_QueryTiming : Migration
{
    public override void Up()
    {
        Create.Table("query_timing").InSchema("audit")
            .WithColumn("query_timing_id").AsInt64().PrimaryKey("pk_query_timing").Identity()
            .WithColumn("correlation_id").AsString(40).NotNullable()
            .WithColumn("seq").AsInt32().NotNullable()
            .WithColumn("command_text").AsString(4000).NotNullable()
            .WithColumn("parameter_names").AsString(1000).Nullable()
            .WithColumn("duration_ms").AsDecimal(12, 2).NotNullable()
            .WithColumn("rows_affected").AsInt32().NotNullable()
            .WithColumn("occurred_at_utc").AsDateTime2().NotNullable();
        Create.Index("ix_query_timing_correlation").OnTable("query_timing").InSchema("audit").OnColumn("correlation_id");
        Create.Index("ix_query_timing_time").OnTable("query_timing").InSchema("audit").OnColumn("occurred_at_utc").Descending();
    }

    public override void Down() => Delete.Table("query_timing").InSchema("audit");
}
