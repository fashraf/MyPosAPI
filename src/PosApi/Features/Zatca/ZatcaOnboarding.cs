using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace PosApi.Features.Zatca;

/// <summary>What the business types in, once.</summary>
public sealed record ZatcaDetails
{
    public string SellerNameAr { get; init; } = "";
    public string SellerNameEn { get; init; } = "";
    public string VatNumber { get; init; } = "";          // 15 digits, starts and ends with 3
    public string CrNumber { get; init; } = "";           // 10 digits (commercial registration)
    public string BuildingNumber { get; init; } = "";     // 4 digits
    public string Street { get; init; } = "";
    public string District { get; init; } = "";
    public string City { get; init; } = "";
    public string PostalCode { get; init; } = "";         // 5 digits
    public string AdditionalNumber { get; init; } = "";   // 4 digits (optional)
    public string BranchName { get; init; } = "";         // organisation unit
    public string Industry { get; init; } = "Restaurant";
    public string DeviceName { get; init; } = "";         // EGS unit name shown in Fatoora
    public string DeviceSerial { get; init; } = "";       // generated once: 1-Wateeq|2-MyPOS|3-<uuid>
    /// <summary>1100 = standard (B2B) and simplified (B2C) invoices.</summary>
    public string InvoiceTypes { get; init; } = "1100";
}

public static partial class ZatcaOnboarding
{
    public static readonly IReadOnlyDictionary<string, string> BaseUrls = new Dictionary<string, string>
    {
        ["sandbox"] = "https://gw-fatoora.zatca.gov.sa/e-invoicing/developer-portal",
        ["simulation"] = "https://gw-fatoora.zatca.gov.sa/e-invoicing/simulation",
        ["production"] = "https://gw-fatoora.zatca.gov.sa/e-invoicing/core",
    };

    /// <summary>Certificate template name ZATCA expects in the CSR for each environment.</summary>
    private static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<string, string>
    {
        ["sandbox"] = "TSTZATCA-Code-Signing",
        ["simulation"] = "PREZATCA-Code-Signing",
        ["production"] = "ZATCA-Code-Signing",
    };

    [GeneratedRegex(@"^3\d{13}3$")] private static partial Regex Vat();
    [GeneratedRegex(@"^\d{10}$")] private static partial Regex Cr();
    [GeneratedRegex(@"^\d{4}$")] private static partial Regex Four();
    [GeneratedRegex(@"^\d{5}$")] private static partial Regex Five();

    /// <summary>Field → problem, in plain words (English and Arabic). Empty when everything is right.</summary>
    public static Dictionary<string, (string En, string Ar)> Validate(ZatcaDetails d)
    {
        var e = new Dictionary<string, (string, string)>();
        if (string.IsNullOrWhiteSpace(d.SellerNameAr) && string.IsNullOrWhiteSpace(d.SellerNameEn))
            e["sellerNameAr"] = ("Enter the business name as registered.", "أدخل اسم المنشأة كما هو مسجل.");
        if (!Vat().IsMatch(d.VatNumber)) e["vatNumber"] = ("15 digits, starting and ending with 3.", "15 رقمًا تبدأ وتنتهي بالرقم 3.");
        if (!Cr().IsMatch(d.CrNumber)) e["crNumber"] = ("10 digits.", "10 أرقام.");
        if (!Four().IsMatch(d.BuildingNumber)) e["buildingNumber"] = ("4 digits (from the national address).", "4 أرقام (من العنوان الوطني).");
        if (string.IsNullOrWhiteSpace(d.Street)) e["street"] = ("Enter the street.", "أدخل الشارع.");
        if (string.IsNullOrWhiteSpace(d.District)) e["district"] = ("Enter the district.", "أدخل الحي.");
        if (string.IsNullOrWhiteSpace(d.City)) e["city"] = ("Enter the city.", "أدخل المدينة.");
        if (!Five().IsMatch(d.PostalCode)) e["postalCode"] = ("5 digits.", "5 أرقام.");
        if (!string.IsNullOrEmpty(d.AdditionalNumber) && !Four().IsMatch(d.AdditionalNumber)) e["additionalNumber"] = ("4 digits, or leave empty.", "4 أرقام أو اتركه فارغًا.");
        if (string.IsNullOrWhiteSpace(d.DeviceName)) e["deviceName"] = ("Name this till, e.g. Main counter.", "سمِّ هذا الجهاز، مثل الكاشير الرئيسي.");
        return e;
    }

    /// <summary>A new secp256k1 key and the CSR ZATCA asks for, as PEM text.</summary>
    public static (string PrivateKeyPem, string CsrPem) CreateCsr(ZatcaDetails d, string environment)
    {
        var curve = SecNamedCurves.GetByName("secp256k1");
        var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var generator = new ECKeyPairGenerator("EC");
        generator.Init(new ECKeyGenerationParameters(new ECNamedDomainParameters(SecObjectIdentifiers.SecP256k1, domain), new SecureRandom()));
        var keys = generator.GenerateKeyPair();

        var org = string.IsNullOrWhiteSpace(d.SellerNameEn) ? d.SellerNameAr : d.SellerNameEn;
        var subject = new X509Name(new List<DerObjectIdentifier> { X509Name.C, X509Name.OU, X509Name.O, X509Name.CN },
            new List<string> { "SA", string.IsNullOrWhiteSpace(d.BranchName) ? org : d.BranchName, org, d.DeviceName });

        /* SN, UID, title, registeredAddress, businessCategory: ZATCA reads these from the subject alternative name. */
        var san = new X509Name(
            new List<DerObjectIdentifier>
            {
                new("2.5.4.4"), new("0.9.2342.19200300.100.1.1"), new("2.5.4.12"), new("2.5.4.26"), new("2.5.4.15"),
            },
            new List<string> { d.DeviceSerial, d.VatNumber, d.InvoiceTypes, ShortAddress(d), d.Industry });
        var extensions = new X509ExtensionsGenerator();
        extensions.AddExtension(new DerObjectIdentifier("1.3.6.1.4.1.311.20.2"), false, new DerPrintableString(Templates[environment]));
        extensions.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(new GeneralName(san)));
        var attributes = new DerSet(new Org.BouncyCastle.Asn1.Pkcs.AttributePkcs(Org.BouncyCastle.Asn1.Pkcs.PkcsObjectIdentifiers.Pkcs9AtExtensionRequest, new DerSet(extensions.Generate())));

        var csr = new Pkcs10CertificationRequest("SHA256withECDSA", subject, keys.Public, attributes, keys.Private);
        return (Pem(keys.Private), Pem(csr));
    }

    public static string ShortAddress(ZatcaDetails d) => $"{d.BuildingNumber} {d.Street}, {d.District}, {d.City} {d.PostalCode}".Trim();

    private static string Pem(object o)
    {
        using var w = new StringWriter();
        var pem = new PemWriter(w);
        pem.WriteObject(o);
        pem.Writer.Flush();
        return w.ToString();
    }

    /* ---------------------------------------------------------------- secrets at rest */

    /// <summary>AES-GCM with a key from configuration (Zatca:SecretKey, else the JWT signing key).</summary>
    public static string Protect(string plain, IConfiguration config)
    {
        var key = SecretKey(config);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public static string Unprotect(string protectedText, IConfiguration config)
    {
        var all = Convert.FromBase64String(protectedText);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(SecretKey(config), 16);
        aes.Decrypt(all[..12], all[28..], all[12..28], plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] SecretKey(IConfiguration config)
    {
        var source = config["Zatca:SecretKey"];
        if (string.IsNullOrWhiteSpace(source)) source = config["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Set Zatca:SecretKey (or Jwt:SigningKey) to store ZATCA keys.");
        return SHA256.HashData(Encoding.UTF8.GetBytes("zatca-secrets|" + source));
    }
}
