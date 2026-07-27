namespace RemSound.Core;

/// <summary>
/// The gate for NEW profile passwords (2026-07-27, alongside the PBKDF2 raise). The password is the
/// ONLY thing standing between a captured stream and an offline guessing rig — the fingerprint
/// travels in cleartext, so a short or common password falls in seconds no matter how slow we make
/// the derivation. Deliberately simple and predictable (no scoring meter — a screen-reader user
/// gets one clear rule and one concrete suggestion): at least <see cref="MinLength"/> characters
/// and not an infamous password. Existing saved passwords are grandfathered — the gate fires only
/// when a password is being SET or CHANGED, so nobody's working setup breaks; they meet the rule
/// the next time they choose to change it.
/// </summary>
public static class PasswordStrength
{
    public const int MinLength = 8;

    // The classics that appear at the top of every breached-password list. Not a dictionary —
    // just the entries so common that allowing them makes the length rule meaningless.
    private static readonly string[] CommonPasswords =
    {
        "password", "password1", "12345678", "123456789", "1234567890", "qwertyui", "qwerty123",
        "11111111", "iloveyou", "sunshine", "letmein1", "trustno1", "remsound",
    };

    /// <summary>Null when the password is acceptable; otherwise ONE plain-English paragraph
    /// telling the user exactly what to do instead. Empty input returns null — clearing a
    /// password is a separate, deliberate act with its own gate.</summary>
    public static string? Critique(string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        // Kept short on purpose (Ed, 2026-07-27): one line — the rule and a concrete example to copy
        // the shape of. The surrounding dialogs add the "why" and the "how to change it".
        if (password.Length < MinLength)
            return $"Use at least {MinLength} characters — three unrelated words with a number, like kettle9tiger42moon, works well.";
        foreach (var common in CommonPasswords)
        {
            if (string.Equals(password, common, StringComparison.OrdinalIgnoreCase))
                return $"That's one of the most common passwords in the world. Use at least {MinLength} characters — three unrelated words with a number, like kettle9tiger42moon, works well.";
        }
        return null;
    }
}
