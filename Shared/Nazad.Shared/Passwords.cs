using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace Nazad.Shared;

/// <summary>
/// One password format for both applications.
///
/// It was written twice — once in Nazad, once in POSSA — and two copies of a
/// hashing routine drift. The day one is hardened and the other is not, the
/// weaker one is the one an attacker uses.
/// </summary>
public static class Passwords
{
    /* OWASP's current floor for PBKDF2-HMAC-SHA256. Raising it here raises it
       for both applications at once, which is the point of sharing it. */
    public const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (string Hash, string Salt) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);

        var hash = KeyDerivation.Pbkdf2(
            password, salt, KeyDerivationPrf.HMACSHA256, Iterations, HashBytes);

        return ($"pbkdf2${Iterations}${Convert.ToBase64String(hash)}",
                Convert.ToBase64String(salt));
    }

    /// <summary>
    /// Whether the password matches.
    ///
    /// Compared in constant time: a comparison that returns early leaks how much
    /// of a guess was right, one byte at a time.
    /// </summary>
    public static bool Verify(string password, string stored, string salt)
    {
        var parts = stored.Split('$');

        if (parts.Length != 3 || parts[0] != "pbkdf2"
            || !int.TryParse(parts[1], out var iterations)
            /* A stored hash claiming a low iteration count is either corrupt or
               an attempt to make verification cheap. Refuse rather than honour it. */
            || iterations < 100_000)
        {
            return false;
        }

        byte[] expected, saltBytes;

        try
        {
            expected = Convert.FromBase64String(parts[2]);
            saltBytes = Convert.FromBase64String(salt);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = KeyDerivation.Pbkdf2(
            password, saltBytes, KeyDerivationPrf.HMACSHA256, iterations, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
