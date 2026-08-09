using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CloudEmuera.Infrastructure.Identity;

public sealed class IdentityValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static partial class IdentityValidation
{
    private static readonly IdnMapping Idn = new();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public static string NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 64 || !UsernamePattern().IsMatch(username)
            || username.Contains("..", StringComparison.Ordinal) || username[0] == '.' || username[^1] == '.')
            throw new IdentityValidationException("INVALID_USERNAME");
        return username.ToUpperInvariant();
    }

    public static string NormalizeEmail(string email)
    {
        string trimmed = email?.Trim() ?? string.Empty;
        if (trimmed.Length is < 3 or > 254 || trimmed.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new IdentityValidationException("INVALID_EMAIL");
        int at = trimmed.LastIndexOf('@');
        if (at <= 0 || at != trimmed.IndexOf('@') || at == trimmed.Length - 1)
            throw new IdentityValidationException("INVALID_EMAIL");
        string local = trimmed[..at];
        string domain = trimmed[(at + 1)..];
        try { domain = Idn.GetAscii(domain); }
        catch (ArgumentException) { throw new IdentityValidationException("INVALID_EMAIL"); }
        if (local.Length > 64 || domain.Length is < 3 or > 253 || domain.Contains(' ') || !domain.Contains('.', StringComparison.Ordinal))
            throw new IdentityValidationException("INVALID_EMAIL");
        return string.Concat(local.ToUpperInvariant(), "@", domain.ToUpperInvariant());
    }

    public static void ValidatePassword(string password)
    {
        if (password is null || password.Contains('\0')) throw new IdentityValidationException("INVALID_PASSWORD");
        int scalarCount;
        try { scalarCount = password.EnumerateRunes().Count(); }
        catch (ArgumentException) { throw new IdentityValidationException("INVALID_PASSWORD"); }
        if (scalarCount is < 12 or > 128) throw new IdentityValidationException("INVALID_PASSWORD");
    }
}
