using System.Data;
using System.Text.Json.Nodes;
using FluentMigrator;

namespace PosApi.Data.Migrations;

/// <summary>
/// The first device to sign in uploaded its starter data to the shared store, including sample records that are not
/// the business's own: four sample discounts, four sample vendors, three commission staff and sample staff documents.
/// Remove exactly those (by their sample ids) and bump each revision, so every device takes the cleaned copy.
/// Anything the owner created is kept.
/// </summary>
[Migration(2026112604, "Remove sample data from the shared store")]
public sealed class M2026112604_RemoveSampleData : Migration
{
    private static readonly HashSet<string> SampleIds = new(StringComparer.Ordinal)
    {
        "dsc_001", "dsc_002", "dsc_003", "dsc_004",
        "ven_001", "ven_002", "ven_003", "ven_004",
        "stf_001", "stf_002", "stf_003",
    };

    public override void Up() => Execute.WithConnection((conn, tx) =>
    {
        var rows = new List<(string Name, long Revision, string Json)>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT slice_name, revision, data_json FROM core.tenant_store";
            using var r = read.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), Convert.ToInt64(r.GetValue(1)), r.GetString(2)));
        }

        foreach (var (name, revision, json) in rows)
        {
            if (JsonNode.Parse(json) is not JsonObject data) continue;
            var changed = name switch
            {
                "discounts" => Strip(data, "discounts"),
                "inventory" => Strip(data, "vendors"),
                "catalog" => Strip(data, "staff"),
                "access" => ClearDocuments(data),
                _ => false,
            };
            if (!changed) continue;

            using var write = conn.CreateCommand();
            write.Transaction = tx;
            write.CommandText = "UPDATE core.tenant_store SET data_json = @data, revision = @rev WHERE slice_name = @name";
            Add(write, "@data", data.ToJsonString());
            Add(write, "@rev", revision + 1);
            Add(write, "@name", name);
            write.ExecuteNonQuery();
        }
    });

    public override void Down() { }

    private static bool Strip(JsonObject data, string list)
    {
        if (data[list] is not JsonArray items) return false;
        var sample = items.Where(i => i is JsonObject o && o["id"]?.GetValue<string>() is { } id && SampleIds.Contains(id)).ToList();
        foreach (var item in sample) items.Remove(item);
        return sample.Count > 0;
    }

    private static bool ClearDocuments(JsonObject data)
    {
        if (data["users"] is not JsonArray users) return false;
        var changed = false;
        foreach (var u in users.OfType<JsonObject>())
        {
            if (u["documents"] is JsonArray docs && docs.Count > 0) { u["documents"] = new JsonArray(); changed = true; }
            /* Sample "last active" times and nationalities from the starter data. */
            if (u["lastActiveAt"]?.ToString() is "2026-11-15T07:15:00.000Z" or "2026-10-02T11:20:00.000Z") { u["lastActiveAt"] = null; changed = true; }
            if (u.ContainsKey("nationality")) { u.Remove("nationality"); changed = true; }
        }
        return changed;
    }

    private static void Add(IDbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
