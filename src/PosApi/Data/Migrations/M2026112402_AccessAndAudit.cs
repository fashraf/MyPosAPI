using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>
/// Company, branches, users, roles, permissions, sign-in (OTP, refresh tokens, attempts) and the audit trail.
/// Same columns as the original SQL Server schema, in lower_snake_case. Rules that used SQL Server-only syntax
/// (VAT/CR digit checks) are enforced in C#; simple value lists stay as portable CHECK constraints.
/// </summary>
[Migration(2026112402, "Access and audit")]
public sealed class M2026112402_AccessAndAudit : Migration
{
    public override void Up()
    {
        Create.Schema("core");
        Create.Schema("audit");

        Create.Table("company").InSchema("core")
            .WithColumn("company_id").AsInt32().PrimaryKey("pk_company").Identity()
            .WithColumn("name_ar").AsString(160).NotNullable()
            .WithColumn("name_en").AsString(160).NotNullable()
            .WithColumn("description_ar").AsString(400).Nullable()
            .WithColumn("description_en").AsString(400).Nullable()
            .WithColumn("logo_url").AsString(400).Nullable()
            .WithColumn("cr_number").AsString(10).Nullable()
            .WithColumn("vat_number").AsString(15).Nullable()
            .WithColumn("phone").AsString(30).Nullable()
            .WithColumn("email").AsString(160).Nullable()
            .WithColumn("website").AsString(160).Nullable()
            .WithColumn("street").AsString(160).Nullable()
            .WithColumn("district").AsString(120).Nullable()
            .WithColumn("city").AsString(120).Nullable()
            .WithColumn("postal_code").AsString(12).Nullable()
            .WithColumn("country").AsString(80).Nullable().WithDefaultValue("SA")
            .WithColumn("created_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Table("branch").InSchema("core")
            .WithColumn("branch_id").AsString(40).PrimaryKey("pk_branch")
            .WithColumn("code").AsString(20).NotNullable().Unique("uq_branch_code")
            .WithColumn("name_ar").AsString(160).NotNullable()
            .WithColumn("name_en").AsString(160).NotNullable()
            .WithColumn("phone").AsString(30).Nullable()
            .WithColumn("address_ar").AsString(300).Nullable()
            .WithColumn("address_en").AsString(300).Nullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Table("branch_settings").InSchema("core")
            .WithColumn("branch_id").AsString(40).PrimaryKey("pk_branch_settings")
                .ForeignKey("fk_branch_settings_branch", "core", "branch", "branch_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("default_customer_mode").AsString(20).NotNullable().WithDefaultValue("walkin")
            .WithColumn("served_by_requirement").AsString(20).NotNullable().WithDefaultValue("optional")
            .WithColumn("allow_negative_stock").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("default_bill_id").AsString(40).Nullable()
            .WithColumn("auto_print_customer").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("auto_print_groups").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("vat_rate_percent").AsDecimal(5, 2).NotNullable().WithDefaultValue(15.00m)
            .WithColumn("prices_include_vat").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("enabled_order_types").AsString(200).NotNullable().WithDefaultValue("dine_in,takeaway,delivery")
            .WithColumn("default_order_type").AsString(20).NotNullable().WithDefaultValue("takeaway")
            .WithColumn("kitchen_routing_json").AsString(400).Nullable()
            .WithColumn("receipt_timing_json").AsString(400).Nullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_by").AsString(120).Nullable();

        Create.Table("permission").InSchema("core")
            .WithColumn("permission_key").AsString(60).PrimaryKey("pk_permission")
            .WithColumn("module").AsString(40).NotNullable()
            .WithColumn("action").AsString(40).NotNullable()
            .WithColumn("sensitivity").AsString(20).NotNullable().WithDefaultValue("general");

        Create.Table("role").InSchema("core")
            .WithColumn("role_id").AsString(40).PrimaryKey("pk_role")
            .WithColumn("name_ar").AsString(120).NotNullable()
            .WithColumn("name_en").AsString(120).NotNullable()
            .WithColumn("description_ar").AsString(400).Nullable()
            .WithColumn("description_en").AsString(400).Nullable()
            .WithColumn("is_built_in").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("is_external").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Table("role_permission").InSchema("core")
            .WithColumn("role_id").AsString(40).PrimaryKey("pk_role_permission")
                .ForeignKey("fk_role_permission_role", "core", "role", "role_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("permission_key").AsString(60).PrimaryKey("pk_role_permission")
                .ForeignKey("fk_role_permission_permission", "core", "permission", "permission_key");

        Create.Table("app_user").InSchema("core")
            .WithColumn("user_id").AsString(40).PrimaryKey("pk_app_user")
            .WithColumn("username").AsString(60).NotNullable().Unique("uq_app_user_username")
            .WithColumn("name_ar").AsString(160).NotNullable()
            .WithColumn("name_en").AsString(160).NotNullable()
            .WithColumn("email").AsString(160).Nullable()
            .WithColumn("phone").AsString(30).Nullable()
            .WithColumn("role_id").AsString(40).NotNullable().ForeignKey("fk_app_user_role", "core", "role", "role_id")
            .WithColumn("employee_no").AsInt32().Nullable()
            .WithColumn("password_hash").AsString(400).Nullable()
            .WithColumn("password_salt").AsString(200).Nullable()
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("active")
            .WithColumn("is_external").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("access_expires_on").AsDate().Nullable()
            .WithColumn("base_salary_h").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("last_login_at_utc").AsDateTime2().Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(120).Nullable()
            .WithColumn("updated_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_by").AsString(120).Nullable();
        Create.Index("ix_app_user_email").OnTable("app_user").InSchema("core").OnColumn("email");
        Create.Index("ix_app_user_phone").OnTable("app_user").InSchema("core").OnColumn("phone");

        Create.Table("user_branch").InSchema("core")
            .WithColumn("user_id").AsString(40).PrimaryKey("pk_user_branch")
                .ForeignKey("fk_user_branch_user", "core", "app_user", "user_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("branch_id").AsString(40).PrimaryKey("pk_user_branch")
                .ForeignKey("fk_user_branch_branch", "core", "branch", "branch_id");

        Create.Table("user_permission_override").InSchema("core")
            .WithColumn("user_id").AsString(40).PrimaryKey("pk_user_permission_override")
                .ForeignKey("fk_user_override_user", "core", "app_user", "user_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("permission_key").AsString(60).PrimaryKey("pk_user_permission_override")
                .ForeignKey("fk_user_override_permission", "core", "permission", "permission_key")
            .WithColumn("effect").AsString(10).NotNullable();

        Create.Table("otp_challenge").InSchema("core")
            .WithColumn("challenge_id").AsString(64).PrimaryKey("pk_otp_challenge")
            .WithColumn("identifier").AsString(160).NotNullable()
            .WithColumn("purpose").AsString(40).NotNullable()
            .WithColumn("code_hash").AsString(400).NotNullable()
            .WithColumn("code_salt").AsString(200).NotNullable()
            .WithColumn("expires_at_utc").AsDateTime2().NotNullable()
            .WithColumn("attempts").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("consumed_at_utc").AsDateTime2().Nullable()
            .WithColumn("created_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Create.Index("ix_otp_identifier").OnTable("otp_challenge").InSchema("core").OnColumn("identifier");

        Create.Table("refresh_token").InSchema("core")
            .WithColumn("token_hash").AsString(200).PrimaryKey("pk_refresh_token")
            .WithColumn("user_id").AsString(40).NotNullable()
                .ForeignKey("fk_refresh_token_user", "core", "app_user", "user_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("issued_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("expires_at_utc").AsDateTime2().NotNullable()
            .WithColumn("revoked_at_utc").AsDateTime2().Nullable()
            .WithColumn("user_agent").AsString(300).Nullable()
            .WithColumn("ip_address").AsString(60).Nullable();
        Create.Index("ix_refresh_token_user").OnTable("refresh_token").InSchema("core").OnColumn("user_id");

        Create.Table("login_attempt").InSchema("core")
            .WithColumn("attempt_id").AsInt64().PrimaryKey("pk_login_attempt").Identity()
            .WithColumn("identifier").AsString(160).NotNullable()
            .WithColumn("succeeded").AsBoolean().NotNullable()
            .WithColumn("ip_address").AsString(60).Nullable()
            .WithColumn("user_agent").AsString(300).Nullable()
            .WithColumn("attempted_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Table("audit_entry").InSchema("audit")
            .WithColumn("audit_id").AsInt64().PrimaryKey("pk_audit_entry").Identity()
            .WithColumn("user_id").AsString(40).Nullable()
            .WithColumn("username").AsString(120).Nullable()
            .WithColumn("role_id").AsString(40).Nullable()
            .WithColumn("action").AsString(30).NotNullable()
            .WithColumn("module").AsString(60).NotNullable()
            .WithColumn("entity_type").AsString(60).Nullable()
            .WithColumn("entity_id").AsString(80).Nullable()
            .WithColumn("api_path").AsString(300).Nullable()
            .WithColumn("http_method").AsString(10).Nullable()
            .WithColumn("current_page").AsString(200).Nullable()
            .WithColumn("previous_page").AsString(200).Nullable()
            .WithColumn("branch_id").AsString(40).Nullable()
            .WithColumn("old_value").AsString(int.MaxValue).Nullable()
            .WithColumn("new_value").AsString(int.MaxValue).Nullable()
            .WithColumn("details").AsString(int.MaxValue).Nullable()
            .WithColumn("result").AsString(20).NotNullable().WithDefaultValue("success")
            .WithColumn("status_code").AsInt32().Nullable()
            .WithColumn("error_message").AsString(600).Nullable()
            .WithColumn("ip_address").AsString(60).Nullable()
            .WithColumn("user_agent").AsString(400).Nullable()
            .WithColumn("correlation_id").AsString(40).Nullable()
            .WithColumn("occurred_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Create.Index("ix_audit_time").OnTable("audit_entry").InSchema("audit").OnColumn("occurred_at_utc").Descending();
        Create.Index("ix_audit_user").OnTable("audit_entry").InSchema("audit").OnColumn("user_id").Ascending().OnColumn("occurred_at_utc").Descending();

        Create.Table("audit_change").InSchema("audit")
            .WithColumn("change_id").AsInt64().PrimaryKey("pk_audit_change").Identity()
            .WithColumn("audit_id").AsInt64().NotNullable()
                .ForeignKey("fk_audit_change_entry", "audit", "audit_entry", "audit_id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("schema_name").AsString(40).NotNullable()
            .WithColumn("table_name").AsString(80).NotNullable()
            .WithColumn("operation").AsString(10).NotNullable()
            .WithColumn("primary_key").AsString(200).Nullable()
            .WithColumn("foreign_key").AsString(200).Nullable()
            .WithColumn("foreign_table").AsString(120).Nullable()
            .WithColumn("column_name").AsString(80).Nullable()
            .WithColumn("old_value").AsString(400).Nullable()
            .WithColumn("new_value").AsString(400).Nullable()
            .WithColumn("rows_affected").AsInt32().Nullable();

        Create.Table("request_timing").InSchema("audit")
            .WithColumn("timing_id").AsInt64().PrimaryKey("pk_request_timing").Identity()
            .WithColumn("correlation_id").AsString(40).NotNullable()
            .WithColumn("user_id").AsString(40).Nullable()
            .WithColumn("username").AsString(120).Nullable()
            .WithColumn("api_path").AsString(300).NotNullable()
            .WithColumn("http_method").AsString(10).NotNullable()
            .WithColumn("current_page").AsString(200).Nullable()
            .WithColumn("previous_page").AsString(200).Nullable()
            .WithColumn("total_ms").AsInt32().NotNullable()
            .WithColumn("database_ms").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("query_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("status_code").AsInt32().NotNullable()
            .WithColumn("succeeded").AsBoolean().NotNullable()
            .WithColumn("error_message").AsString(600).Nullable()
            .WithColumn("response_bytes").AsInt32().Nullable()
            .WithColumn("occurred_at_utc").AsDateTime2().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Create.Index("ix_timing_time").OnTable("request_timing").InSchema("audit").OnColumn("occurred_at_utc").Descending();

        // Portable value-list rules (same text works on PostgreSQL and SQL Server).
        foreach (var (table, name, rule) in new[]
        {
            ("core.branch_settings", "ck_bs_mode", "default_customer_mode IN ('walkin','customer')"),
            ("core.branch_settings", "ck_bs_served_by", "served_by_requirement IN ('off','optional','required')"),
            ("core.branch_settings", "ck_bs_vat", "vat_rate_percent >= 0 AND vat_rate_percent <= 100"),
            ("core.permission", "ck_permission_sensitivity", "sensitivity IN ('general','financial','personal')"),
            ("core.app_user", "ck_app_user_status", "status IN ('active','suspended','expired')"),
            ("core.app_user", "ck_app_user_salary", "base_salary_h >= 0"),
            ("core.user_permission_override", "ck_user_override_effect", "effect IN ('grant','deny')"),
            ("core.otp_challenge", "ck_otp_purpose", "purpose IN ('login','register','reset','verify')"),
            ("core.otp_challenge", "ck_otp_attempts", "attempts >= 0"),
            ("audit.audit_entry", "ck_audit_result", "result IN ('success','failure','denied')"),
            ("audit.audit_change", "ck_audit_change_op", "operation IN ('INSERT','UPDATE','DELETE')"),
            ("audit.request_timing", "ck_timing_ms", "total_ms >= 0 AND database_ms >= 0"),
        })
        {
            Execute.Sql($"ALTER TABLE {table} ADD CONSTRAINT {name} CHECK ({rule})");
        }
    }

    public override void Down()
    {
        foreach (var t in new[] { "request_timing", "audit_change", "audit_entry" }) Delete.Table(t).InSchema("audit");
        foreach (var t in new[] { "login_attempt", "refresh_token", "otp_challenge", "user_permission_override", "user_branch",
                                  "app_user", "role_permission", "role", "permission", "branch_settings", "branch", "company" })
            Delete.Table(t).InSchema("core");
        Delete.Schema("audit");
        Delete.Schema("core");
    }
}
