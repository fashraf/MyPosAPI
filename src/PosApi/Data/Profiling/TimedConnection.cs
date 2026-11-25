using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace PosApi.Data.Profiling;

/// <summary>One SQL statement a request ran.</summary>
public sealed record QueryEntry(int Seq, string CommandText, string ParameterNames, double DurationMs, int RowsAffected, DateTime AtUtc);

/// <summary>
/// The statements of the current HTTP request. Set by the audit middleware at the start of a request; every command
/// on a <see cref="TimedConnection"/> adds itself. Outside a request (background writers, the migrator) it is null and
/// nothing is recorded.
/// </summary>
public sealed class RequestQueryLog
{
    private static readonly AsyncLocal<RequestQueryLog?> CurrentLog = new();
    private readonly List<QueryEntry> _entries = new();
    private readonly object _gate = new();

    public static RequestQueryLog? Current
    {
        get => CurrentLog.Value;
        set => CurrentLog.Value = value;
    }

    public IReadOnlyList<QueryEntry> Entries { get { lock (_gate) return _entries.ToList(); } }
    public double TotalMs { get { lock (_gate) return _entries.Sum(e => e.DurationMs); } }

    internal void Add(string sql, DbParameterCollection parameters, double ms, int rows)
    {
        var names = string.Join(", ", parameters.Cast<DbParameter>().Select(p => p.ParameterName));
        /* Keep the statement readable and bounded; parameter VALUES are never stored (they can be personal data). */
        var text = sql.Length > 4000 ? sql[..4000] : sql;
        lock (_gate)
        {
            if (_entries.Count >= 200) return; // a runaway loop should not fill memory
            _entries.Add(new QueryEntry(_entries.Count + 1, text, names, Math.Round(ms, 2), rows, DateTime.UtcNow));
        }
    }
}

/// <summary>A connection that times every command it runs. Transparent to Dapper and SqlKata.</summary>
public sealed class TimedConnection(DbConnection inner) : DbConnection
{
    internal DbConnection Inner { get; } = inner;

    [AllowNull]
    public override string ConnectionString { get => Inner.ConnectionString; set => Inner.ConnectionString = value; }
    public override string Database => Inner.Database;
    public override string DataSource => Inner.DataSource;
    public override string ServerVersion => Inner.ServerVersion;
    public override ConnectionState State => Inner.State;
    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);
    public override void Close() => Inner.Close();
    public override void Open() => Inner.Open();
    public override Task OpenAsync(CancellationToken cancellationToken) => Inner.OpenAsync(cancellationToken);
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => Inner.BeginTransaction(isolationLevel);
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
        await Inner.BeginTransactionAsync(isolationLevel, cancellationToken);
    protected override DbCommand CreateDbCommand() => new TimedCommand(Inner.CreateCommand(), this);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => Inner.DisposeAsync();
}

internal sealed class TimedCommand(DbCommand inner, TimedConnection owner) : DbCommand
{
    private TimedConnection? _owner = owner;

    [AllowNull]
    public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
    public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
    public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
    protected override DbParameterCollection DbParameterCollection => inner.Parameters;
    protected override DbParameter CreateDbParameter() => inner.CreateParameter();
    public override void Cancel() => inner.Cancel();
    public override void Prepare() => inner.Prepare();

    protected override DbConnection? DbConnection
    {
        get => _owner;
        set
        {
            _owner = value as TimedConnection;
            inner.Connection = value is TimedConnection timed ? timed.Inner : value;
        }
    }

    protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }

    private T Time<T>(Func<T> run, Func<T, int> rows)
    {
        var log = RequestQueryLog.Current;
        if (log is null) return run();
        var watch = Stopwatch.StartNew();
        var result = run();
        log.Add(inner.CommandText ?? "", inner.Parameters, watch.Elapsed.TotalMilliseconds, rows(result));
        return result;
    }

    private async Task<T> TimeAsync<T>(Func<Task<T>> run, Func<T, int> rows)
    {
        var log = RequestQueryLog.Current;
        if (log is null) return await run();
        var watch = Stopwatch.StartNew();
        var result = await run();
        log.Add(inner.CommandText ?? "", inner.Parameters, watch.Elapsed.TotalMilliseconds, rows(result));
        return result;
    }

    public override int ExecuteNonQuery() => Time(inner.ExecuteNonQuery, n => n);
    public override object? ExecuteScalar() => Time(inner.ExecuteScalar, _ => -1);
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Time(() => inner.ExecuteReader(behavior), r => r.RecordsAffected);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        TimeAsync(() => inner.ExecuteNonQueryAsync(cancellationToken), n => n);
    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
        TimeAsync(() => inner.ExecuteScalarAsync(cancellationToken), _ => -1);
    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        TimeAsync(() => inner.ExecuteReaderAsync(behavior, cancellationToken), r => r.RecordsAffected);

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
