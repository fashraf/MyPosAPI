using System.Text.Json;
using Dapper;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;

namespace PosApi.Features.Devices;

public sealed record PrintJobRequest(string PrinterId, string Document, string Reference, int? Copies, string PayloadBase64);

/// <summary>
/// Printers and cloud printing.
///
/// The POS renders the receipt itself (ESC/POS text or raster) and, for a cloud
/// printer, posts the bytes here. The printer polls with its MAC/serial:
///   Star CloudPRNT:   POST /cloudprnt (poll) → GET /cloudprnt?mac= (job) → DELETE /cloudprnt?mac=&amp;code= (result)
/// Epson Server Direct Print works the same way through its own XML envelope
/// (not implemented yet — see docs/restaurant-sessions-and-tax.md, printing).
/// Poll endpoints are anonymous by design (printers cannot hold a JWT); they
/// only ever reveal jobs for the printer whose CloudId matches.
/// </summary>
public static class PrinterEndpoints
{
    public static void MapPrinterEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Printers").RequireAuthorization().RequireRateLimiting("standard");
        g.MapGet("/printers", ListAsync);
        g.MapPut("/printers", SaveAsync).RequireAuthorization(PermissionPolicy.For("settings.edit"));
        g.MapPost("/print-jobs", EnqueueAsync).RequireAuthorization(PermissionPolicy.For("pos.view"));
        g.MapGet("/print-jobs", JobsAsync).RequireAuthorization(PermissionPolicy.For("settings.view"));

        var cloud = app.MapGroup("/cloudprnt").WithTags("CloudPRNT").AllowAnonymous().RequireRateLimiting("standard");
        cloud.MapPost("", PollAsync);
        cloud.MapGet("", FetchAsync);
        cloud.MapDelete("", ConfirmAsync);
    }

    private static async Task<IResult> ListAsync(string? branchId, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var rows = await c.QueryAsync(new CommandDefinition(
            "SELECT * FROM prn.Printer WHERE (@b IS NULL OR BranchId = @b OR BranchId IS NULL) ORDER BY Name;", new { b = branchId }, cancellationToken: ct));
        return Results.Ok(rows);
    }

    private static async Task<IResult> SaveAsync(JsonElement p, ISqlConnectionFactory db, CancellationToken ct)
    {
        string S(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        string List(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? string.Join(',', v.EnumerateArray().Select(x => x.GetString())) : "";
        var id = string.IsNullOrWhiteSpace(S("id")) ? $"prn_{Guid.NewGuid():N}" : S("id");
        if (string.IsNullOrWhiteSpace(S("name")))
            return Results.Json(ApiError.Create("validation_failed", "Give the printer a name.", "أدخل اسم الطابعة."), statusCode: 422);

        await using var c = db.Create();
        await c.ExecuteAsync(new CommandDefinition(
            """
            MERGE prn.Printer AS t USING (SELECT @id AS PrinterId) AS s ON t.PrinterId = s.PrinterId
            WHEN MATCHED THEN UPDATE SET BranchId=@branch, Name=@name, Connection=@conn, PaperWidth=@paper, RenderMode=@mode, DataSaver=@saver,
                 Copies=@copies, Documents=@docs, DefaultFor=@defaults, Host=@host, Port=@port, Protocol=@proto, Url=@url, CloudId=NULLIF(@cloud,''),
                 Status=@status, UpdatedAtUtc=SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (PrinterId, BranchId, Name, Connection, PaperWidth, RenderMode, DataSaver, Copies, Documents, DefaultFor, Host, Port, Protocol, Url, CloudId, Status)
                 VALUES (@id, @branch, @name, @conn, @paper, @mode, @saver, @copies, @docs, @defaults, @host, @port, @proto, @url, NULLIF(@cloud,''), @status);
            /* One default per document per branch. */
            UPDATE prn.Printer SET DefaultFor = (
                SELECT STRING_AGG(value, ',') FROM STRING_SPLIT(DefaultFor, ',') WHERE value <> '' AND value NOT IN (SELECT value FROM STRING_SPLIT(@defaults, ',')))
             WHERE PrinterId <> @id AND ISNULL(BranchId, '') = ISNULL(@branch, '');
            """,
            new
            {
                id, branch = NullIfEmpty(S("branchId")), name = S("name"), conn = S("connection"),
                paper = p.TryGetProperty("paperWidth", out var pw) ? pw.GetInt32() : 80, mode = S("mode") is "" ? "auto" : S("mode"),
                saver = p.TryGetProperty("dataSaver", out var ds) && ds.GetBoolean(), copies = p.TryGetProperty("copies", out var cp) ? cp.GetInt32() : 1,
                docs = List("documents"), defaults = List("defaultFor"), host = NullIfEmpty(S("host")),
                port = p.TryGetProperty("port", out var po) && po.ValueKind == JsonValueKind.Number ? po.GetInt32() : (int?)null,
                proto = NullIfEmpty(S("protocol")), url = NullIfEmpty(S("url")), cloud = S("cloudId"), status = S("status") is "" ? "active" : S("status"),
            }, cancellationToken: ct));
        return Results.Ok(new { printerId = id });
    }

    private static async Task<IResult> EnqueueAsync(PrintJobRequest r, ISqlConnectionFactory db, ICurrentUser me, CancellationToken ct)
    {
        byte[] payload;
        try { payload = Convert.FromBase64String(r.PayloadBase64); }
        catch (FormatException) { return Results.Json(ApiError.Create("bad_payload", "The print data is not valid.", "بيانات الطباعة غير صالحة."), statusCode: 422); }
        if (payload.Length > 512 * 1024)
            return Results.Json(ApiError.Create("too_large", "The receipt is too large to print.", "الإيصال أكبر من المسموح."), statusCode: 413);

        var jobId = $"pjb_{Guid.NewGuid():N}";
        await using var c = db.Create();
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT prn.PrintJob (JobId, PrinterId, Document, Reference, Copies, Payload, CreatedBy) VALUES (@jobId, @PrinterId, @Document, @Reference, @copies, @payload, @by);",
            new { jobId, r.PrinterId, r.Document, r.Reference, copies = Math.Clamp(r.Copies ?? 1, 1, 5), payload, by = me.Username }, cancellationToken: ct));
        return Results.Created($"/api/print-jobs/{jobId}", new { jobId });
    }

    private static async Task<IResult> JobsAsync(ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var rows = await c.QueryAsync(new CommandDefinition(
            "SELECT TOP (100) JobId, PrinterId, Document, Reference, DATALENGTH(Payload) AS Bytes, Status, CreatedAtUtc, PrintedAtUtc, Error FROM prn.PrintJob ORDER BY CreatedAtUtc DESC;",
            cancellationToken: ct));
        return Results.Ok(rows);
    }

    /* ----------------------------------------------------------- CloudPRNT */

    private static async Task<IResult> PollAsync(JsonElement body, ISqlConnectionFactory db, CancellationToken ct)
    {
        var mac = body.TryGetProperty("printerMAC", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(mac)) return Results.Ok(new { jobReady = false });
        await using var c = db.Create();
        var ready = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE prn.Printer SET LastSeenAtUtc = SYSUTCDATETIME() WHERE CloudId = @mac;
            SELECT COUNT(*) FROM prn.PrintJob j JOIN prn.Printer p ON p.PrinterId = j.PrinterId WHERE p.CloudId = @mac AND j.Status = N'queued';
            """, new { mac }, cancellationToken: ct));
        return Results.Ok(new { jobReady = ready > 0, mediaTypes = new[] { "application/vnd.star.starprnt" } });
    }

    private static async Task<IResult> FetchAsync(string mac, ISqlConnectionFactory db, CancellationToken ct)
    {
        await using var c = db.Create();
        var job = await c.QuerySingleOrDefaultAsync<(string JobId, byte[] Payload, int Copies)>(new CommandDefinition(
            "prn.usp_NextJob", new { CloudId = mac }, commandType: System.Data.CommandType.StoredProcedure, cancellationToken: ct));
        if (job.Payload is null) return Results.NoContent();
        var bytes = Enumerable.Repeat(job.Payload, Math.Max(1, job.Copies)).SelectMany(b => b).ToArray();
        return Results.File(bytes, "application/vnd.star.starprnt");
    }

    private static async Task<IResult> ConfirmAsync(string mac, string? code, ISqlConnectionFactory db, CancellationToken ct)
    {
        var ok = code is null || code.StartsWith('2');
        await using var c = db.Create();
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE TOP (1) j SET Status = CASE WHEN @ok = 1 THEN N'printed' ELSE N'failed' END,
                   PrintedAtUtc = CASE WHEN @ok = 1 THEN SYSUTCDATETIME() END, Error = CASE WHEN @ok = 1 THEN NULL ELSE @code END
              FROM prn.PrintJob j JOIN prn.Printer p ON p.PrinterId = j.PrinterId
             WHERE p.CloudId = @mac AND j.Status = N'printing';
            """, new { mac, ok, code }, cancellationToken: ct));
        return Results.Ok();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
