using System.Text;

namespace CloudOrc.Agent.Tray;

/// <summary>
/// Activation code rules from §4 ("6–8 character code"): 6 to 8 letters/digits, optionally
/// split by a single hyphen (e.g. "ABCD-1234"), case-insensitive. This is only a local
/// convenience check - the server is the authority on whether a code is valid, unexpired
/// and within its attempt limit.
/// </summary>
public static class ActivationCodeFormat
{
    public const int MinCharacters = 6;
    public const int MaxCharacters = 8;

    /// <summary>Uppercases and removes all whitespace. Returns empty for null.</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Validates an already-normalized code.</summary>
    public static bool IsValid(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        var alphanumericCount = 0;
        var hyphenCount = 0;
        foreach (var c in normalized)
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                alphanumericCount++;
            }
            else if (c == '-')
            {
                hyphenCount++;
            }
            else
            {
                return false;
            }
        }

        if (hyphenCount > 1)
        {
            return false;
        }

        if (hyphenCount == 1 && (normalized[0] == '-' || normalized[^1] == '-'))
        {
            return false;
        }

        return alphanumericCount is >= MinCharacters and <= MaxCharacters;
    }

    /// <summary>Normalizes then validates. Returns the normalized code on success.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = Normalize(input);
        return IsValid(normalized);
    }
}
