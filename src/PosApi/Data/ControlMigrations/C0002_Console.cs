using System.Data;
using System.Text.Json;
using FluentMigrator;

namespace PosApi.Data.ControlMigrations;

/// <summary>
/// What the POSSA console needs: each client's commercial profile (plan, trial, renewal, contact, payment methods),
/// the lists clients are built from (cities, business types, payment methods, deactivation reasons) and the plans,
/// as tables. The first rows below are starting data, edited later in POSSA; nothing is hard-coded in the console.
/// Also the default super admin "admin" (password admin@123, must be changed at first sign-in).
/// </summary>
[Migration(2, "Control: console profile, master data, plans, notifications")]
public sealed class C0002_Console : Migration
{
    public override void Up()
    {
        Alter.Table("tenant").InSchema("control")
            .AddColumn("name_ar").AsString(200).Nullable()
            .AddColumn("business_type").AsString(60).Nullable()
            .AddColumn("city").AsString(60).Nullable()
            .AddColumn("plan_code").AsString(40).Nullable()
            .AddColumn("billing_cycle").AsString(20).Nullable()
            .AddColumn("is_trial").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("trial_days").AsInt32().Nullable()
            .AddColumn("subscription_starts").AsDateTime2().Nullable()
            .AddColumn("subscription_ends").AsDateTime2().Nullable()
            .AddColumn("mrr_h").AsInt64().NotNullable().WithDefaultValue(0)
            .AddColumn("balance_h").AsInt64().NotNullable().WithDefaultValue(0)
            .AddColumn("poc_username").AsString(60).Nullable()
            .AddColumn("poc_role").AsString(40).Nullable()
            .AddColumn("payment_methods").AsString(400).Nullable()
            .AddColumn("vat_rate").AsInt32().NotNullable().WithDefaultValue(15)
            .AddColumn("prices_include_vat").AsBoolean().NotNullable().WithDefaultValue(true)
            .AddColumn("cr_number").AsString(20).Nullable()
            .AddColumn("vat_number").AsString(20).Nullable()
            .AddColumn("registered_by").AsString(200).Nullable()
            .AddColumn("activated_by").AsString(200).Nullable()
            .AddColumn("activated_at_utc").AsDateTime2().Nullable()
            .AddColumn("renewals").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("last_renewed_at_utc").AsDateTime2().Nullable()
            .AddColumn("status_reason").AsString(400).Nullable();

        Alter.Table("tenant_event").InSchema("control")
            .AddColumn("label").AsString(200).Nullable()
            .AddColumn("before_value").AsString(400).Nullable()
            .AddColumn("after_value").AsString(400).Nullable()
            .AddColumn("reason").AsString(1000).Nullable()
            .AddColumn("result").AsString(20).Nullable()
            .AddColumn("ip").AsString(60).Nullable();

        Alter.Table("admin_user").InSchema("control")
            .AddColumn("username").AsString(60).Nullable().Unique("ux_admin_username")
            .AddColumn("must_change_password").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("role").AsString(40).NotNullable().WithDefaultValue("owner")
            .AddColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true);

        Create.Table("master_item").InSchema("control")
            .WithColumn("list_key").AsString(40).NotNullable().PrimaryKey("pk_master_item")
            .WithColumn("code").AsString(60).NotNullable().PrimaryKey("pk_master_item")
            .WithColumn("name_en").AsString(200).NotNullable()
            .WithColumn("name_ar").AsString(200).Nullable()
            .WithColumn("extra_json").AsString(2000).Nullable()
            .WithColumn("sort").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true);

        Create.Table("plan").InSchema("control")
            .WithColumn("code").AsString(40).PrimaryKey("pk_plan")
            .WithColumn("name").AsString(100).NotNullable()
            .WithColumn("monthly_h").AsInt64().Nullable()
            .WithColumn("annual_h").AsInt64().Nullable()
            .WithColumn("is_trial").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("trial_days").AsInt32().Nullable()
            .WithColumn("limits_json").AsString(2000).NotNullable()      // branches, devices, users, transactions, storageGb (null = unlimited)
            .WithColumn("features_json").AsString(4000).NotNullable()
            .WithColumn("sort").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true);

        Create.Table("notification").InSchema("control")
            .WithColumn("notification_id").AsInt64().PrimaryKey("pk_notification").Identity()
            .WithColumn("tenant_id").AsInt64().Nullable()
            .WithColumn("kind").AsString(40).NotNullable()
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("body").AsString(1000).Nullable()
            .WithColumn("severity").AsString(20).NotNullable()
            .WithColumn("is_read").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("at_utc").AsDateTime2().NotNullable();

        Execute.WithConnection((conn, tx) =>
        {
            void Item(string list, string code, string en, string? ar, object? extra, int sort) => Exec(conn, tx,
                "INSERT INTO control.master_item (list_key, code, name_en, name_ar, extra_json, sort, is_active) VALUES (@l, @c, @e, @a, @x, @s, @t)",
                ("@l", list), ("@c", code), ("@e", en), ("@a", (object?)ar ?? DBNull.Value),
                ("@x", extra is null ? DBNull.Value : JsonSerializer.Serialize(extra)), ("@s", sort), ("@t", true));

            var cities = new (string, string, string)[]
            {
                ("Riyadh", "الرياض", "Riyadh"), ("Jeddah", "جدة", "Makkah"), ("Makkah", "مكة المكرمة", "Makkah"), ("Madinah", "المدينة المنورة", "Madinah"),
                ("Dammam", "الدمام", "Eastern"), ("Khobar", "الخبر", "Eastern"), ("Dhahran", "الظهران", "Eastern"), ("Tabuk", "تبوك", "Tabuk"),
                ("Abha", "أبها", "Asir"), ("Buraydah", "بريدة", "Qassim"), ("Hail", "حائل", "Hail"), ("Najran", "نجران", "Najran"),
                ("Jubail", "الجبيل", "Eastern"), ("Yanbu", "ينبع", "Madinah"), ("Taif", "الطائف", "Makkah"),
            };
            for (var i = 0; i < cities.Length; i++) Item("city", cities[i].Item1, cities[i].Item1, cities[i].Item2, new { region = cities[i].Item3 }, i);

            var services = new (string, string, string, string)[]
            {
                ("Laundry", "Laundry", "مغسلة", "Shirt"), ("Retail", "Retail shop", "متجر تجزئة", "ShoppingBag"), ("Restaurant", "Restaurant", "مطعم", "UtensilsCrossed"),
                ("Barber", "Barber shop", "حلاق", "Scissors"), ("Grocery", "Grocery", "بقالة", "ShoppingCart"), ("Pharmacy", "Pharmacy", "صيدلية", "Pill"),
                ("Cafe", "Cafe", "مقهى", "Coffee"), ("CarWash", "Car wash", "مغسلة سيارات", "Car"), ("Bakery", "Bakery", "مخبز", "Croissant"),
            };
            for (var i = 0; i < services.Length; i++) Item("service", services[i].Item1, services[i].Item2, services[i].Item3, new { icon = services[i].Item4 }, i);

            var methods = new (string, string, string, string)[]
            {
                ("Cash", "Cash", "نقدي", "cash"), ("Mada", "Mada", "مدى", "card"), ("Visa", "Visa", "فيزا", "card"), ("Mastercard", "Mastercard", "ماستركارد", "card"),
                ("ApplePay", "Apple Pay", "أبل باي", "wallet"), ("STCPay", "STC Pay", "إس تي سي باي", "wallet"), ("BankTransfer", "Bank transfer", "تحويل بنكي", "bank"),
                ("Credit", "Store credit", "رصيد المتجر", "credit"),
            };
            for (var i = 0; i < methods.Length; i++) Item("payment_method", methods[i].Item1, methods[i].Item2, methods[i].Item3, new { kind = methods[i].Item4 }, i);

            var reasons = new (string, string, string)[]
            {
                ("non_payment", "Non-payment", "عدم السداد"), ("requested", "Client requested closure", "طلب العميل الإغلاق"),
                ("fraud", "Suspected fraud or misuse", "اشتباه في احتيال"), ("trial_ended", "Trial ended without conversion", "انتهت الفترة التجريبية"),
                ("duplicate", "Duplicate account", "حساب مكرر"), ("migration", "Moved to another account", "نُقل إلى حساب آخر"), ("other", "Other — see comment", "أخرى — انظر التعليق"),
            };
            for (var i = 0; i < reasons.Length; i++) Item("deactivation_reason", reasons[i].Item1, reasons[i].Item2, reasons[i].Item3, null, i);

            void Plan(string code, string name, long? monthly, long? annual, bool trial, int? trialDays, object limits, string[] features, int sort) => Exec(conn, tx,
                "INSERT INTO control.plan (code, name, monthly_h, annual_h, is_trial, trial_days, limits_json, features_json, sort, is_active) VALUES (@c, @n, @m, @a, @t, @d, @l, @f, @s, @x)",
                ("@c", code), ("@n", name), ("@m", (object?)monthly ?? DBNull.Value), ("@a", (object?)annual ?? DBNull.Value), ("@t", trial),
                ("@d", (object?)trialDays ?? DBNull.Value), ("@l", JsonSerializer.Serialize(limits)), ("@f", JsonSerializer.Serialize(features)), ("@s", sort), ("@x", true));
            Plan("Trial", "Free trial", 0, null, true, 15, new { branches = 1, devices = 2, users = 3, transactions = 500, storageGb = 1 }, ["Full POS", "One branch", "Expires automatically"], 0);
            Plan("Basic", "Basic", 29900, 299000, false, null, new { branches = 1, devices = 3, users = 5, transactions = 5000, storageGb = 5 }, ["POS", "Inventory", "Basic reports"], 1);
            Plan("Pro", "Pro", 79900, 799000, false, null, new { branches = 5, devices = 20, users = 25, transactions = 25000, storageGb = 20 }, ["Everything in Basic", "Multi-branch", "Payroll", "API access"], 2);
            Plan("Enterprise", "Enterprise", null, null, false, null, new { branches = (int?)null, devices = (int?)null, users = (int?)null, transactions = (int?)null, storageGb = (int?)null }, ["Everything in Pro", "Unlimited everything", "Priority support"], 3);

            /* The default super admin, only on an empty control database. */
            using var count = conn.CreateCommand();
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM control.admin_user";
            if (Convert.ToInt64(count.ExecuteScalar()) == 0)
            {
                var (hash, salt) = new PosApi.Auth.PasswordHasher().Hash("admin@123");
                Exec(conn, tx, "INSERT INTO control.admin_user (email, name, password_hash, password_salt, created_at_utc, username, must_change_password, role, is_active) " +
                    "VALUES (@e, @n, @h, @s, @at, @u, @m, @r, @a)",
                    ("@e", "admin@possa.local"), ("@n", "Platform admin"), ("@h", hash), ("@s", salt), ("@at", DateTime.UtcNow), ("@u", "admin"), ("@m", true), ("@r", "owner"), ("@a", true));
            }
        });
    }

    public override void Down()
    {
        Delete.Table("notification").InSchema("control");
        Delete.Table("plan").InSchema("control");
        Delete.Table("master_item").InSchema("control");
    }

    private static void Exec(IDbConnection conn, IDbTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        cmd.ExecuteNonQuery();
    }
}
