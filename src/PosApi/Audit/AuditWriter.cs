using System.Threading.Channels;
using Dapper;
using PosApi.Data;

namespace PosApi.Audit;

public interface IAuditWriter
{
    void Record(AuditEntry entry);
    void RecordTiming(RequestTiming timing);
}

/// <summary>
/// Writes audit and timing rows without making the caller wait.
///
/// Both are observations about a request, not part of it. Writing them inline
/// would add a database round trip to every call and — worse — a failure to
/// record an observation would fail the operation being observed. Queued here
/// and drained by a background reader instead.
///
/// The queue is bounded and drops the oldest entry when full. Unbounded would
/// mean a database outage turns into memory exhaustion, and telemetry taking
/// the process down is a worse outcome than losing telemetry.
/// </summary>
public sealed class AuditWriter : IAuditWriter
{
    private readonly Channel<object> _queue;
    private readonly ILogger<AuditWriter> _logger;

    public AuditWriter(ILogger<AuditWriter> logger)
    {
        _logger = logger;
        _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    internal ChannelReader<object> Reader => _queue.Reader;

    public void Record(AuditEntry entry)
    {
        if (!_queue.Writer.TryWrite(entry))
        {
            _logger.LogWarning("Audit queue is full; an entry was dropped.");
        }
    }

    public void RecordTiming(RequestTiming timing) => _queue.Writer.TryWrite(timing);
}

/// <summary>Drains the queue into SQL Server.</summary>
public sealed class AuditDrainService : BackgroundService
{
    private readonly AuditWriter _writer;
    private readonly PosApi.Data.IDb _db;
    private readonly ILogger<AuditDrainService> _logger;

    public AuditDrainService(
        IAuditWriter writer, PosApi.Data.IDb db, ILogger<AuditDrainService> logger)
    {
        _writer = (AuditWriter)writer;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _writer.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var connection = await _db.OpenAsync(stoppingToken);

                if (item is AuditEntry entry)
                {
                    var auditId = await connection.ExecuteScalarAsync<long>(
                        _db.Provider.Dialect.InsertReturningId(
                        """
                        INSERT INTO audit.audit_entry
                            (user_id, username, role_id, action, module, entity_type, entity_id,
                             api_path, http_method, current_page, previous_page, branch_id,
                             old_value, new_value, details, result, status_code, error_message,
                             ip_address, user_agent, correlation_id, occurred_at_utc)
                        VALUES
                            (@UserId, @Username, @RoleId, @Action, @Module, @EntityType, @EntityId,
                             @ApiPath, @HttpMethod, @CurrentPage, @PreviousPage, @BranchId,
                             @OldValue, @NewValue, @Details, @Result, @StatusCode, @ErrorMessage,
                             @IpAddress, @UserAgent, @CorrelationId, CURRENT_TIMESTAMP)
                        """, "audit_id"), entry);

                    /* SCOPE_IDENTITY, so these attach to this entry rather than
                       whatever anyone else inserted most recently. */
                    foreach (var change in entry.Changes)
                    {
                        await connection.ExecuteAsync(
                            """
                            INSERT INTO audit.audit_change
                                (audit_id, schema_name, table_name, operation, primary_key,
                                 foreign_key, foreign_table, column_name, old_value,
                                 new_value, rows_affected)
                            VALUES
                                (@AuditId, @SchemaName, @TableName, @Operation, @PrimaryKey,
                                 @ForeignKey, @ForeignTable, @ColumnName, @OldValue,
                                 @NewValue, @RowsAffected)
                            """,
                            new
                            {
                                AuditId = auditId,
                                change.SchemaName, change.TableName, change.Operation,
                                change.PrimaryKey, change.ForeignKey, change.ForeignTable,
                                change.ColumnName, change.OldValue, change.NewValue,
                                change.RowsAffected
                            });
                    }
                }
                else if (item is RequestTiming timing)
                {
                    await connection.ExecuteAsync(
                        """
                        INSERT INTO audit.request_timing
                            (correlation_id, user_id, username, api_path, http_method,
                             current_page, previous_page, total_ms, database_ms, query_count,
                             status_code, succeeded, error_message, response_bytes)
                        VALUES
                            (@CorrelationId, @UserId, @Username, @ApiPath, @HttpMethod,
                             @CurrentPage, @PreviousPage, @TotalMs, @DatabaseMs, @QueryCount,
                             @StatusCode, @Succeeded, @ErrorMessage, @ResponseBytes)
                        """, timing);
                }
            }
            catch (Exception ex)
            {
                /* Never rethrow. A failure to record an observation must not
                   stop the drain, and must certainly not affect the request
                   that produced it — which has already completed. */
                _logger.LogError(ex, "Could not write an audit or timing row.");
            }
        }
    }
}
