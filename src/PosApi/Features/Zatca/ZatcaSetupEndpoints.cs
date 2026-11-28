using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PosApi.Auth;
using PosApi.Common;
using PosApi.Data;
using SqlKata.Execution;

namespace PosApi.Features.Zatca;

/// <summary>
/// ZATCA (Fatoora) onboarding in five steps the owner can follow:
/// 1 details → 2 device and environment → 3 generate the request (CSR) → 4 connect with the OTP from Fatoora
/// (compliance certificate) → 5 go live (production certificate). Keys and certificates never leave the server.
/// </summary>
public static class ZatcaSetupEndpoints
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record SaveBody(string Environment, ZatcaDetails Details);
    public sealed record OtpBody(string Otp);

    public static void MapZatcaSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/zatca/setup").WithTags("ZATCA setup").RequireRateLimiting("standard")
            .RequireAuthorization(PermissionPolicy.For("settings.edit"));

        g.MapGet("", GetSetupAsync);

        /* Steps 1 and 2: details, device and environment. Changing them after a certificate starts over. */
        g.MapPut("", SaveSetupAsync);

        /* Step 3: a fresh key and certificate request. */
        g.MapPost("/csr", CsrAsync);

        /* Step 4: the OTP from Fatoora → compliance certificate (CSID). */
        g.MapPost("/onboard", OnboardAsync);

        /* Step 5: production certificate, from the compliance one. */
        g.MapPost("/production", ProductionAsync);
    }

    internal static async Task<IResult> GetSetupAsync(IDb db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await Load(db, conn, ct);
        if (row is null)
        {
            /* Nothing saved yet: start from the company profile so most fields are already filled. */
            var company = (await db.Query(conn).Query("core.company").OrderBy("company_id").GetAsync(cancellationToken: ct))
                .Cast<IDictionary<string, object?>>().FirstOrDefault();
            var draft = new ZatcaDetails
            {
                SellerNameAr = Col(company, "name_ar") ?? "",
                SellerNameEn = Col(company, "name_en") ?? "",
                VatNumber = Col(company, "vat_number") ?? "",
                CrNumber = Col(company, "cr_number") ?? "",
                City = Col(company, "city") ?? "",
                DeviceName = "Main counter",
            };
            return Results.Ok(new { environment = "sandbox", status = "draft", details = draft, steps = Steps("draft", false, saved: false), lastError = (string?)null });
        }
        return Results.Ok(View(row));
    }

    internal static async Task<IResult> SaveSetupAsync(SaveBody body, IDb db, ICurrentUser user, CancellationToken ct)
    {
        if (!ZatcaOnboarding.BaseUrls.ContainsKey(body.Environment))
            return Results.Json(ApiError.Create("bad_environment", "Choose Sandbox, Simulation or Production.", "اختر بيئة الاختبار أو المحاكاة أو الإنتاج."), statusCode: 422);
        var details = body.Details with
        {
            VatNumber = body.Details.VatNumber.Trim(), CrNumber = body.Details.CrNumber.Trim(),
            PostalCode = body.Details.PostalCode.Trim(), BuildingNumber = body.Details.BuildingNumber.Trim(),
        };
        var errors = ZatcaOnboarding.Validate(details);
        if (errors.Count > 0)
            return Results.Json(new
            {
                code = "invalid", messageEn = "Some details need fixing.", messageAr = "بعض البيانات تحتاج تصحيحًا.",
                fieldErrors = errors.ToDictionary(e => e.Key, e => new[] { e.Value.En }),
                fieldErrorsAr = errors.ToDictionary(e => e.Key, e => new[] { e.Value.Ar }),
            }, statusCode: 422);

        await using var conn = await db.OpenAsync(ct);
        var existing = await Load(db, conn, ct);
        var old = existing is null ? null : JsonSerializer.Deserialize<ZatcaDetails>((string)existing["details_json"]!, Json);
        if (string.IsNullOrWhiteSpace(details.DeviceSerial))
            details = details with { DeviceSerial = old?.DeviceSerial is { Length: > 0 } s ? s : $"1-Wateeq|2-MyPOS|3-{Guid.NewGuid()}" };
        /* Anything the certificate is built from changed (or the environment): the old certificate no longer fits. */
        var reset = existing is null || (string)existing["environment"]! != body.Environment
            || (old is null ? "" : JsonSerializer.Serialize(old with { DeviceSerial = "" }, Json)) != JsonSerializer.Serialize(details with { DeviceSerial = "" }, Json);
        var values = new Dictionary<string, object?>
        {
            ["environment"] = body.Environment, ["details_json"] = JsonSerializer.Serialize(details, Json),
            ["updated_at_utc"] = DateTime.UtcNow, ["updated_by"] = user.Username, ["last_error"] = null,
        };
        if (reset)
        {
            values["status"] = "draft"; values["private_key_enc"] = null; values["csr_pem"] = null;
            values["compliance_json_enc"] = null; values["production_json_enc"] = null;
        }
        if (existing is null) { values["setup_id"] = 1; await db.Query(conn).Query("core.zatca_setup").InsertAsync(values, cancellationToken: ct); }
        else await db.Query(conn).Query("core.zatca_setup").Where("setup_id", 1).UpdateAsync(values, cancellationToken: ct);
        return Results.Ok(View((await Load(db, conn, ct))!));
    }

    internal static async Task<IResult> CsrAsync(IDb db, IConfiguration config, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await Load(db, conn, ct);
        if (row is null) return NotReady("Save the business details first.", "احفظ بيانات المنشأة أولًا.");
        var details = JsonSerializer.Deserialize<ZatcaDetails>((string)row["details_json"]!, Json)!;
        var (key, csr) = ZatcaOnboarding.CreateCsr(details, (string)row["environment"]!);
        await db.Query(conn).Query("core.zatca_setup").Where("setup_id", 1).UpdateAsync(new Dictionary<string, object?>
        {
            ["private_key_enc"] = ZatcaOnboarding.Protect(key, config), ["csr_pem"] = csr, ["status"] = "csr_ready",
            ["compliance_json_enc"] = null, ["production_json_enc"] = null, ["last_error"] = null, ["updated_at_utc"] = DateTime.UtcNow,
        }, cancellationToken: ct);
        return Results.Ok(View((await Load(db, conn, ct))!));
    }

    internal static async Task<IResult> OnboardAsync(OtpBody body, IDb db, IConfiguration config, CancellationToken ct)
    {
        var otp = (body.Otp ?? "").Trim();
        if (otp.Length is < 4 or > 10 || !otp.All(char.IsDigit))
            return Results.Json(ApiError.Create("bad_otp", "Enter the OTP from the Fatoora portal (digits only).", "أدخل رمز التحقق من بوابة فاتورة (أرقام فقط)."), statusCode: 422);
        await using var conn = await db.OpenAsync(ct);
        var row = await Load(db, conn, ct);
        if (row?["csr_pem"] is not string csr) return NotReady("Generate the request first (step 3).", "أنشئ الطلب أولًا (الخطوة 3).");
        var env = (string)row["environment"]!;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ZatcaOnboarding.BaseUrls[env]}/compliance")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { csr = Convert.ToBase64String(Encoding.UTF8.GetBytes(csr)) }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("OTP", otp);
        req.Headers.Add("Accept-Version", "V2");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await CallZatca(db, conn, config, req, "compliance_json_enc", "compliance", ct);
    }

    internal static async Task<IResult> ProductionAsync(IDb db, IConfiguration config, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await Load(db, conn, ct);
        if (row?["compliance_json_enc"] is not string enc) return NotReady("Connect with the OTP first (step 4).", "اربط برمز التحقق أولًا (الخطوة 4).");
        var compliance = JsonNode.Parse(ZatcaOnboarding.Unprotect(enc, config))!;
        var env = (string)row["environment"]!;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ZatcaOnboarding.BaseUrls[env]}/production/csids")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { compliance_request_id = compliance["requestID"]?.ToString() }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{compliance["binarySecurityToken"]}:{compliance["secret"]}")));
        req.Headers.Add("Accept-Version", "V2");
        return await CallZatca(db, conn, config, req, "production_json_enc", "live", ct);
    }

    private static async Task<IResult> CallZatca(IDb db, System.Data.Common.DbConnection conn, IConfiguration config, HttpRequestMessage req,
        string column, string nextStatus, CancellationToken ct)
    {
        string text; int code;
        try
        {
            using var res = await Http.SendAsync(req, ct);
            code = (int)res.StatusCode;
            text = await res.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await Fail(db, conn, $"Could not reach ZATCA: {ex.Message}", ct);
            return Results.Json(ApiError.Create("zatca_unreachable", "Could not reach ZATCA. Check the server's internet and try again.", "تعذر الوصول إلى هيئة الزكاة. تحقق من اتصال الخادم وأعد المحاولة."), statusCode: 502);
        }
        var node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (code is < 200 or >= 300 || node?["binarySecurityToken"] is null)
        {
            var reason = ZatcaMessage(node) ?? $"ZATCA answered {code}.";
            await Fail(db, conn, reason, ct);
            return Results.Json(ApiError.Create("zatca_refused", $"ZATCA refused: {reason}", $"رفضت هيئة الزكاة الطلب: {reason}"), statusCode: 422);
        }
        var keep = new JsonObject
        {
            ["requestID"] = node["requestID"]?.ToString(), ["binarySecurityToken"] = node["binarySecurityToken"]!.ToString(),
            ["secret"] = node["secret"]?.ToString(), ["receivedAtUtc"] = DateTime.UtcNow,
        };
        await db.Query(conn).Query("core.zatca_setup").Where("setup_id", 1).UpdateAsync(new Dictionary<string, object?>
        {
            [column] = ZatcaOnboarding.Protect(keep.ToJsonString(), config), ["status"] = nextStatus, ["last_error"] = null, ["updated_at_utc"] = DateTime.UtcNow,
        }, cancellationToken: ct);
        return Results.Ok(View((await Load(db, conn, ct))!));
    }

    private static string? ZatcaMessage(JsonNode? node)
    {
        if (node is null) return null;
        var errors = node["errors"] as JsonArray ?? node["validationResults"]?["errorMessages"] as JsonArray;
        var first = errors?.OfType<JsonNode>().Select(e => e["message"]?.ToString() ?? e.ToString()).FirstOrDefault();
        return first ?? node["message"]?.ToString() ?? node["dispositionMessage"]?.ToString();
    }

    private static async Task Fail(IDb db, System.Data.Common.DbConnection conn, string reason, CancellationToken ct) =>
        await db.Query(conn).Query("core.zatca_setup").Where("setup_id", 1).UpdateAsync(new Dictionary<string, object?>
            { ["last_error"] = reason.Length > 1900 ? reason[..1900] : reason, ["updated_at_utc"] = DateTime.UtcNow }, cancellationToken: ct);

    private static async Task<IDictionary<string, object?>?> Load(IDb db, System.Data.Common.DbConnection conn, CancellationToken ct) =>
        (await db.Query(conn).Query("core.zatca_setup").Where("setup_id", 1).GetAsync(cancellationToken: ct)).Cast<IDictionary<string, object?>>().FirstOrDefault();

    private static string? Col(IDictionary<string, object?>? row, string key) => row is not null && row.TryGetValue(key, out var v) ? v as string : null;

    private static IResult NotReady(string en, string ar) => Results.Json(ApiError.Create("not_ready", en, ar), statusCode: 409);

    private static object View(IDictionary<string, object?> r)
    {
        var status = (string)r["status"]!;
        return new
        {
            environment = (string)r["environment"]!,
            status,
            details = JsonSerializer.Deserialize<ZatcaDetails>((string)r["details_json"]!, Json),
            csrReady = r["csr_pem"] is string,
            steps = Steps(status, r["csr_pem"] is string, saved: true),
            lastError = r["last_error"] as string,
            updatedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(r["updated_at_utc"]), DateTimeKind.Utc),
        };
    }

    /// <summary>Which of the five steps are done.</summary>
    private static object Steps(string status, bool csr, bool saved) => new
    {
        /* Saved details were validated on save, so step 1 is done as soon as they are stored. */
        details = saved,
        csr,
        compliance = status is "compliance" or "live",
        live = status == "live",
    };
}
