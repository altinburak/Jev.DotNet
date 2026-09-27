using System.Globalization;
using System.Net.Http.Headers;

namespace Jev.DotNet;

internal static class Backoff
{
    /// <summary>Server delay if allowed by the policy, otherwise capped exponential backoff with jitter.</summary>
    public static TimeSpan Delay(int attempt, HttpResponseHeaders? headers, RetryPolicy policy, Func<double> random)
    {
        if (policy.RespectRetryAfter && headers is not null)
        {
            var retryAfter = ParseRetryAfter(headers, DateTimeOffset.UtcNow);
            if (retryAfter is { } d && d <= policy.MaxRetryAfter) return d;
        }
        var exponential = Math.Min(policy.BackoffInitial.TotalMilliseconds * Math.Pow(2, attempt), policy.BackoffMax.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Round(exponential * (1 - random() * policy.BackoffJitter)));
    }

    /// <summary>Parse <c>retry-after-ms</c>, then <c>Retry-After</c> (seconds or HTTP date).</summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        if (headers.TryGetValues("retry-after-ms", out var msValues)
            && double.TryParse(msValues.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            && ms >= 0 && !double.IsInfinity(ms))
            return TimeSpan.FromMilliseconds(ms);

        if (!headers.TryGetValues("retry-after", out var values)) return null;
        var raw = values.FirstOrDefault()?.Trim();
        if (raw is null) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds >= 0 && !double.IsInfinity(seconds) ? TimeSpan.FromSeconds(seconds) : null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date > now ? date - now : TimeSpan.Zero;
        return null;
    }
}
