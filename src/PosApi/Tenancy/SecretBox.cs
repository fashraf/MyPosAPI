using System.Security.Cryptography;
using System.Text;

namespace PosApi.Tenancy;

/// <summary>
/// Encrypts secrets kept in a database (client database passwords). AES-GCM with a key derived from
/// configuration: Secrets:Key, else Jwt:SigningKey. Keep that value safe and the same on every server.
/// </summary>
public static class SecretBox
{
    public static string Protect(string plain, IConfiguration config, string purpose)
    {
        var key = Key(config, purpose);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public static string Unprotect(string text, IConfiguration config, string purpose)
    {
        var all = Convert.FromBase64String(text);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(Key(config, purpose), 16);
        aes.Decrypt(all[..12], all[28..], all[12..28], plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Key(IConfiguration config, string purpose)
    {
        var source = config["Secrets:Key"];
        if (string.IsNullOrWhiteSpace(source)) source = config["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Set Secrets:Key (or Jwt:SigningKey).");
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{purpose}|{source}"));
    }
}
