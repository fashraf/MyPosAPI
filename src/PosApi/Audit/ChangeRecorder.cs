namespace PosApi.Audit;

/// <summary>
/// What a request changed, collected as it happens.
///
/// The middleware knows a call was made; only the handler knows which table it
/// wrote and which field moved. Held on HttpContext.Items so it lives exactly as
/// long as the request.
/// </summary>
public sealed class ChangeRecorder
{
    private const string Key = "AuditChanges";
    private readonly List<AuditChange> _changes = [];

    public IReadOnlyList<AuditChange> Changes => _changes;

    public static ChangeRecorder For(HttpContext context)
    {
        if (context.Items[Key] is ChangeRecorder existing) return existing;
        var recorder = new ChangeRecorder();
        context.Items[Key] = recorder;
        return recorder;
    }

    public static ChangeRecorder? Existing(HttpContext context) =>
        context.Items[Key] as ChangeRecorder;

    public ChangeRecorder Inserted(string schema, string table, string? primaryKey,
                                   string? foreignKey = null, string? foreignTable = null)
    {
        _changes.Add(new AuditChange
        {
            SchemaName = schema, TableName = table, Operation = "INSERT",
            PrimaryKey = primaryKey, ForeignKey = foreignKey, ForeignTable = foreignTable,
            RowsAffected = 1
        });
        return this;
    }

    /// <summary>
    /// A field that moved. Unchanged fields are dropped, so the real edit is not
    /// buried under twenty that did not move — but null and empty stay distinct,
    /// because clearing a field and leaving it blank are different edits.
    /// </summary>
    public ChangeRecorder Updated(string schema, string table, string? primaryKey,
                                  string column, object? oldValue, object? newValue)
    {
        var before = Text(oldValue);
        var after = Text(newValue);
        if (before == after) return this;

        _changes.Add(new AuditChange
        {
            SchemaName = schema, TableName = table, Operation = "UPDATE",
            PrimaryKey = primaryKey, ColumnName = column,
            OldValue = before, NewValue = after, RowsAffected = 1
        });
        return this;
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        string s => s.Length > 400 ? s[..400] : s,
        _ => value.ToString() is { } t ? (t.Length > 400 ? t[..400] : t) : null
    };
}

public sealed record AuditChange
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string Operation { get; init; }
    public string? PrimaryKey { get; init; }
    public string? ForeignKey { get; init; }
    public string? ForeignTable { get; init; }
    public string? ColumnName { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public int? RowsAffected { get; init; }
}
