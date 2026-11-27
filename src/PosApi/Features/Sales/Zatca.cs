using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PosApi.Features.Sales;

/// <summary>
/// ZATCA invoice stamp: a hash over the invoice's canonical content (which includes the previous invoice's hash,
/// forming the chain) and the TLV QR code. Byte-for-byte the same algorithm as the front end's src/lib/zatca.ts,
/// so a receipt stamped here verifies there and the reverse.
/// </summary>
public static class Zatca
{
    /// <summary>PIH of the very first invoice of an EGS unit: Base64 of the hex SHA-256 of "0".</summary>
    public static readonly string GenesisPih =
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData("0"u8.ToArray())).ToLowerInvariant()));

    public sealed record StampLine(string ItemId, decimal Quantity, long UnitPriceH, long DiscountH);

    public static (string InvoiceHash, string QrBase64) Stamp(
        string uuid, long icv, string previousHash, string invoiceNumber, string issuedAt,
        string sellerName, string vatNumber, long totalH, long vatH, IEnumerable<StampLine> lines)
    {
        /* Same key order and number formatting as JSON.stringify in the front end. */
        var canonical = new JsonObject
        {
            ["uuid"] = uuid,
            ["icv"] = icv,
            ["pih"] = previousHash,
            ["number"] = invoiceNumber,
            ["issuedAt"] = issuedAt,
            ["seller"] = vatNumber,
            ["totalH"] = totalH,
            ["vatH"] = vatH,
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject
            {
                ["itemId"] = l.ItemId,
                ["quantity"] = JsNumber(l.Quantity),
                ["unitPriceH"] = l.UnitPriceH,
                ["discountH"] = l.DiscountH,
            }).ToArray()),
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var timestamp = issuedAt.Length > 4 && issuedAt[^5] == '.' && issuedAt.EndsWith('Z') ? issuedAt[..^5] + "Z" : issuedAt;
        var qr = Tlv(sellerName, vatNumber, timestamp, Amount(totalH), Amount(vatH), hash);
        return (hash, qr);
    }

    /// <summary>JSON.stringify prints 2 not 2.0, and 1.5 as 1.5.</summary>
    private static JsonNode JsNumber(decimal value) =>
        value == decimal.Truncate(value) ? JsonValue.Create((long)value) : JsonValue.Create((double)value);

    private static string Amount(long halalas) => (halalas / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Tlv(params string[] values)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < values.Length; i++)
        {
            var value = Encoding.UTF8.GetBytes(values[i]);
            if (value.Length > 255) value = value[..255];
            bytes.Add((byte)(i + 1));
            bytes.Add((byte)value.Length);
            bytes.AddRange(value);
        }
        return Convert.ToBase64String(bytes.ToArray());
    }
}
