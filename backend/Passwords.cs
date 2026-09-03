namespace BcInventory.Api;

/// <summary>
/// One password policy, applied wherever a password is set (AR-11).
///
/// Length does most of the work, so the minimum is raised to 10 and long passphrases are
/// welcome. The rest of the rules exist to stop the handful of passwords people actually
/// choose under pressure — the company name, the account's own e-mail, "Password123" — rather
/// than to impose complexity for its own sake.
/// </summary>
public static class Passwords
{
    public const int MinLength = 10;

    // Not a breach corpus; the shapes that show up in this organisation's own accounts.
    private static readonly string[] Obvious =
    {
        "password", "passw0rd", "qwerty", "111111", "123456", "12345678", "letmein", "welcome",
        "admin", "administrator", "changeme", "secret", "iloveyou", "monkey", "dragon",
        "bcinventory", "inventory", "energiup", "energi-up", "kpncorp", "ptspc", "bontang",
    };

    /// <summary>Null when acceptable, otherwise the reason to show the administrator.</summary>
    public static string? Check(string? password, string? email = null)
    {
        var p = password ?? "";
        if (p.Length < MinLength)
            return $"Password must be at least {MinLength} characters. A short phrase of a few words is easier to remember and harder to guess than a short complicated string.";
        if (p.Length > 200)
            return "Password must be 200 characters or fewer.";
        if (p.Trim() != p)
            return "Password must not begin or end with a space.";

        var lower = p.ToLowerInvariant();
        foreach (var bad in Obvious)
            if (lower.Contains(bad))
                return "Password contains a word that is guessed early in any attack. Choose something unrelated to the system, the company or the site.";

        // The account's own address is the first thing an attacker tries.
        var local = (email ?? "").Split('@')[0].Trim().ToLowerInvariant();
        if (local.Length >= 3 && lower.Contains(local))
            return "Password must not contain the account's e-mail address.";

        // Length substitutes for complexity: below 16 characters ask for some variety.
        if (p.Length < 16)
        {
            int classes = 0;
            if (p.Any(char.IsLower)) classes++;
            if (p.Any(char.IsUpper)) classes++;
            if (p.Any(char.IsDigit)) classes++;
            if (p.Any(c => !char.IsLetterOrDigit(c))) classes++;
            if (classes < 3)
                return "Password needs at least three of: lower case, upper case, digits, symbols — or make it 16 characters or longer instead.";
        }

        if (p.Distinct().Count() < 5)
            return "Password repeats too few distinct characters.";

        return null;
    }
}
