using System.Globalization;
using System.Net;

namespace CloudOrc.ControlAgent.Transport;

/// <summary>Parses an HTTP Retry-After value (delta-seconds or HTTP-date) and bounds it (§8 "respect valid Retry-After").</summary>
public static class RetryAfter
{
    public static readonly TimeSpan Maximum = TimeSpan.FromHours(1);

    public static TimeSpan? Parse(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return Clamp(TimeSpan.FromSeconds(seconds));
        }

        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return Clamp(date - now);
        }

        return null; // invalid → ignored, caller falls back to its own backoff
    }

    public static TimeSpan? FromHeaders(IReadOnlyDictionary<string, IEnumerable<string>>? headers, DateTimeOffset now)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var (name, values) in headers)
        {
            if (string.Equals(name, "Retry-After", StringComparison.OrdinalIgnoreCase))
            {
                return Parse(values.FirstOrDefault(), now);
            }
        }

        return null;
    }

    public static TimeSpan? FromResponse(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return Clamp(delta);
        }

        return header.Date is { } date ? Clamp(date - now) : null;
    }

    private static TimeSpan? Clamp(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > Maximum ? Maximum : value;

    public static bool IsOverload(HttpStatusCode code) =>
        code is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
}
