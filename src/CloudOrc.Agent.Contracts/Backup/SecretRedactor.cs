using System.Text.RegularExpressions;

namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// Last-line scrubber for anything that leaves the agent as text (events, logs from the worker,
/// command results). §10: "no plaintext keys or tokens in logs, UI or support diagnostics".
/// The design already keeps secrets out of these paths; this catches accidents (e.g. an engine
/// echoing an environment variable or a URL with credentials in an error message).
/// </summary>
public static partial class SecretRedactor
{
    private const string Mask = "[REDACTED]";

    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = BearerPattern().Replace(text, "Bearer " + Mask);
        result = KeyValuePattern().Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + Mask);
        result = GoogleTokenPattern().Replace(result, Mask);
        result = UrlCredentialPattern().Replace(result, "${scheme}" + Mask + "@");
        result = AwsKeyPattern().Replace(result, Mask);
        return result.Length > 2000 ? result[..2000] + "..." : result;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-\._~\+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?<key>(?:password|passwd|pwd|secret|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|access[_-]?key[_-]?id|secret[_-]?access[_-]?key|session[_-]?token|KOPIA_PASSWORD|RESTIC_PASSWORD|AWS_SECRET_ACCESS_KEY|AWS_SESSION_TOKEN))(?<sep>""?\s*[:=]\s*""?)[^\s"",;&}]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"ya29\.[A-Za-z0-9\-_\.]+")]
    private static partial Regex GoogleTokenPattern();

    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+\-.]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentialPattern();

    [GeneratedRegex(@"\b(AKIA|ASIA)[A-Z0-9]{16}\b")]
    private static partial Regex AwsKeyPattern();
}
