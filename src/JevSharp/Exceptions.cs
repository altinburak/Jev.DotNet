using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace JevSharp;

/// <summary>Base class for errors raised by this SDK, including invalid configuration or questions.</summary>
public class JevException : Exception
{
    /// <summary>Create an exception with a message.</summary>
    public JevException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>The API returned a non-success status code (after any retries).</summary>
public class JevApiException : JevException
{
    private const int MaxRawBodyInMessage = 200;

    /// <summary>Create an API error.</summary>
    public JevApiException(HttpStatusCode statusCode, string? body, HttpResponseHeaders headers, string? message = null)
        : base(message ?? Describe(statusCode, body))
    {
        StatusCode = statusCode;
        Body = body;
        Headers = headers;
        RequestId = RequestIds.From(headers);
    }

    /// <summary>The HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The raw response body, or <c>null</c> when empty.</summary>
    public string? Body { get; }

    /// <summary>The response headers.</summary>
    public HttpResponseHeaders Headers { get; }

    /// <summary>The <c>x-typesafe-request-id</c> header, when present.</summary>
    public string? RequestId { get; }

    internal static JevApiException FromResponse(HttpStatusCode status, string? body, HttpResponseHeaders headers) => (int)status switch
    {
        400 => new JevBadRequestException(status, body, headers),
        401 => new JevAuthenticationException(status, body, headers),
        403 => new JevPermissionDeniedException(status, body, headers),
        404 => new JevNotFoundException(status, body, headers),
        422 => new JevValidationException(status, body, headers),
        429 => new JevRateLimitException(status, body, headers),
        >= 500 => new JevServerException(status, body, headers),
        _ => new JevApiException(status, body, headers),
    };

    private static string Describe(HttpStatusCode status, string? body)
    {
        var code = (int)status;
        if (string.IsNullOrEmpty(body)) return $"{code} status code (no body)";
        var detail = ExtractMessage(body);
        if (detail is not null) return $"{code} {detail}";
        return body.Length > MaxRawBodyInMessage ? $"{code} {body[..MaxRawBodyInMessage]}…" : $"{code} {body}";
    }

    /// <summary>Pull a human-readable message out of common error body shapes, including validation lists.</summary>
    private static string? ExtractMessage(string body)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object) return null;
        if (TryString(root, "error", out var s)) return s;
        if (root.TryGetProperty("error", out var error) && TryString(error, "message", out s)) return s;
        if (TryString(root, "message", out s)) return s;
        if (TryString(root, "detail", out s)) return s;
        if (!root.TryGetProperty("detail", out var detail)) return null;
        if (TryString(detail, "message", out s)) return s;
        if (detail.ValueKind != JsonValueKind.Array) return null;

        var parts = new List<string>();
        foreach (var e in detail.EnumerateArray())
        {
            if (!TryString(e, "msg", out var msg)) continue;
            var loc = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("loc", out var l) && l.ValueKind == JsonValueKind.Array
                ? string.Join(".", l.EnumerateArray().Select(x => x.ToString()).Where(x => x != "body"))
                : "";
            parts.Add(loc.Length > 0 ? $"{loc}: {msg}" : msg!);
        }
        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    private static bool TryString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return false;
        value = p.GetString();
        return true;
    }
}

/// <summary>HTTP 400: the request is invalid.</summary>
public sealed class JevBadRequestException(HttpStatusCode s, string? b, HttpResponseHeaders h) : JevApiException(s, b, h);

/// <summary>HTTP 401: the API key is missing or invalid.</summary>
public sealed class JevAuthenticationException(HttpStatusCode s, string? b, HttpResponseHeaders h) : JevApiException(s, b, h);

/// <summary>HTTP 403: access is denied.</summary>
public sealed class JevPermissionDeniedException(HttpStatusCode s, string? b, HttpResponseHeaders h) : JevApiException(s, b, h);

/// <summary>HTTP 404: the resource was not found.</summary>
public sealed class JevNotFoundException(HttpStatusCode s, string? b, HttpResponseHeaders h) : JevApiException(s, b, h);

/// <summary>HTTP 422: the request body failed validation. The message names the offending fields.</summary>
public sealed class JevValidationException(HttpStatusCode s, string? b, HttpResponseHeaders h) : JevApiException(s, b, h);

/// <summary>HTTP 429: the rate limit was exceeded and retries were exhausted.</summary>
public sealed class JevRateLimitException(HttpStatusCode s, string? b, HttpResponseHeaders h) : JevApiException(s, b, h)
{
    /// <summary>How long the server asked to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; } = Backoff.ParseRetryAfter(h, DateTimeOffset.UtcNow);
}

/// <summary>HTTP 5xx, including 529 Overloaded: the server could not handle the request.</summary>
public sealed class JevServerException(HttpStatusCode s, string? b, HttpResponseHeaders h) : JevApiException(s, b, h);

/// <summary>The request could not be sent or the response could not be read (DNS, TLS, connection reset...).</summary>
public class JevConnectionException : JevException
{
    /// <summary>Create a connection error.</summary>
    public JevConnectionException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>An attempt did not complete within the configured timeout.</summary>
public sealed class JevTimeoutException : JevConnectionException
{
    /// <summary>Create a timeout error.</summary>
    public JevTimeoutException(TimeSpan timeout, Exception? innerException = null)
        : base($"Request timed out after {timeout.TotalMilliseconds:0}ms.", innerException) => Timeout = timeout;

    /// <summary>The per-attempt timeout that was exceeded.</summary>
    public TimeSpan Timeout { get; }
}

internal static class RequestIds
{
    public const string Header = "x-typesafe-request-id";

    public static string? From(HttpResponseHeaders headers) =>
        headers.TryGetValues(Header, out var values) ? values.FirstOrDefault() : null;
}
