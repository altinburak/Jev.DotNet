using System.Text.Json;

namespace Jev.DotNet;

/// <summary>
/// Client configuration. Unset values fall back to environment variables, then SDK defaults:
/// <c>TYPESAFE_API_KEY</c>, <c>TYPESAFE_BASE_URL</c> (<c>https://api.typesafe.ai</c>),
/// and <c>TYPESAFE_DEFAULT_MODEL</c> (<c>jev-latest</c>).
/// </summary>
public sealed class JevClientOptions
{
    /// <summary>Default API root.</summary>
    public const string DefaultBaseUrl = "https://api.typesafe.ai";

    /// <summary>Default model alias: the latest stable Jev release.</summary>
    public const string DefaultModelName = "jev-latest";

    /// <summary>API key. Falls back to <c>TYPESAFE_API_KEY</c>. Keep it server-side.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// API root. Falls back to <c>TYPESAFE_BASE_URL</c>, then <see cref="DefaultBaseUrl"/>.
    /// Any server following the TypeSafe API works, e.g. <c>https://openrouter.ai/api</c>.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Model used when a request does not set one. Falls back to <c>TYPESAFE_DEFAULT_MODEL</c>,
    /// then <see cref="DefaultModelName"/>. Pin a versioned id (e.g. <c>jev-1.13.0</c>) if you
    /// have tuned thresholds against a specific version.
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>Timeout for each attempt, including reading the body. Retries get their own timeout. Default: 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Retry behavior for rate limits, server errors, timeouts, and connection failures.</summary>
    public RetryPolicy Retry { get; set; } = RetryPolicy.Default;

    /// <summary>Headers added to every request. Cannot override authentication or content headers.</summary>
    public IDictionary<string, string> DefaultHeaders { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// Serializer settings for <c>state</c>, instructions, and criteria objects. By default property
    /// names are sent exactly as declared, so backticked references like <c>`ticket.body`</c> must
    /// match your C# member names; set a naming policy here to change that.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }
}

/// <summary>
/// When and how to retry. Retries use capped exponential backoff with jitter and honor
/// <c>Retry-After</c> / <c>retry-after-ms</c> headers.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>The default policy: 2 retries, 0.5 s initial backoff doubling up to 5 s.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>A policy that never retries.</summary>
    public static RetryPolicy None { get; } = new() { MaxRetries = 0 };

    /// <summary>Retries after the first attempt; 0 disables retries. Default: 2.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>First backoff delay, doubled each retry up to <see cref="BackoffMax"/>. Default: 500 ms.</summary>
    public TimeSpan BackoffInitial { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Maximum backoff delay. Default: 5 seconds.</summary>
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Fraction of each backoff randomly subtracted, from 0 to 1. Default: 0.25.</summary>
    public double BackoffJitter { get; init; } = 0.25;

    /// <summary>Status codes to retry. Default: 408, 429, and 500–599 (including 529 Overloaded).</summary>
    public IReadOnlySet<int> RetryStatusCodes { get; init; } =
        new HashSet<int>(new[] { 408, 429 }.Concat(Enumerable.Range(500, 100)));

    /// <summary>Wait as long as the server's <c>Retry-After</c> asks, up to <see cref="MaxRetryAfter"/>. Default: true.</summary>
    public bool RespectRetryAfter { get; init; } = true;

    /// <summary>Longest server-requested delay to honor; longer ones fall back to backoff. Default: 60 seconds.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Retry connection failures. Default: true.</summary>
    public bool RetryConnectionErrors { get; init; } = true;

    /// <summary>Retry attempts that exceed the timeout. Default: true.</summary>
    public bool RetryTimeouts { get; init; } = true;

    internal void Validate()
    {
        if (MaxRetries < 0) throw new JevException($"{nameof(MaxRetries)} must be non-negative, got {MaxRetries}.");
        if (BackoffInitial < TimeSpan.Zero) throw new JevException($"{nameof(BackoffInitial)} must be non-negative.");
        if (BackoffMax < TimeSpan.Zero) throw new JevException($"{nameof(BackoffMax)} must be non-negative.");
        if (MaxRetryAfter < TimeSpan.Zero) throw new JevException($"{nameof(MaxRetryAfter)} must be non-negative.");
        if (BackoffJitter is < 0 or > 1 || double.IsNaN(BackoffJitter))
            throw new JevException($"{nameof(BackoffJitter)} must be between 0 and 1, got {BackoffJitter}.");
    }
}

/// <summary>Per-call overrides of client settings.</summary>
public sealed class RequestOptions
{
    /// <summary>Timeout for each attempt of this call.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Retry policy for this call.</summary>
    public RetryPolicy? Retry { get; set; }

    /// <summary>Extra headers for this call, merged over <see cref="JevClientOptions.DefaultHeaders"/>.</summary>
    public IDictionary<string, string>? Headers { get; set; }
}
