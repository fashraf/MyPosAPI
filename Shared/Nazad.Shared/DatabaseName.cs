using System.Text.RegularExpressions;

namespace Nazad.Shared;

/// <summary>
/// A database name that is about to reach SQL.
///
/// Both applications build three-part names from a stored database name —
/// POSSA to read a customer's data, Nazad to reach the control database. The
/// rule belongs in one place, because a second copy is a second chance to get
/// it slightly wrong.
/// </summary>
public static partial class DatabaseName
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex Allowed();

    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Allowed().IsMatch(name);

    /// <summary>
    /// The name, bracketed, or an exception.
    ///
    /// Throwing rather than returning null: a caller that ignores a null builds
    /// the query anyway, and the whole purpose of this is that the bad name
    /// never reaches the query.
    /// </summary>
    public static string Bracket(string? name) =>
        IsValid(name)
            ? $"[{name}]"
            : throw new ArgumentException(
                $"'{name}' is not a valid database name. Letters, digits and underscores only.",
                nameof(name));
}
