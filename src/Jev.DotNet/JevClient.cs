using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jev.DotNet;

/// <summary>Client for TypeSafe's System One API (Jev).</summary>
public interface IJevClient
{
    /// <summary>Evaluate state against named questions and return typed answers.</summary>
    /// <exception cref="JevException">The questions are invalid.</exception>
    /// <exception cref="JevApiException">The API returned an error after retries.</exception>
    /// <exception cref="JevConnectionException">The request failed to connect or timed out after retries.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<SystemOneResponse> SystemOneAsync(SystemOneRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Evaluate <paramref name="state"/> against named questions and return typed answers.</summary>
    Task<SystemOneResponse> SystemOneAsync(object? state, IReadOnlyDictionary<string, Question> questions, string? model = null, CancellationToken cancellationToken = default);

    /// <summary>List the models and aliases the account can use.</summary>
    Task<IReadOnlyList<ModelCard>> ListModelsAsync(RequestOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Client for TypeSafe's System One API (Jev). Thread-safe; create one and reuse it, or register
/// it with <c>services.AddJevClient()</c>.
/// </summary>
/// <example>
/// <code>
/// using var client = new JevClient(); // reads TYPESAFE_API_KEY
/// var result = await client.SystemOneAsync(
///     "I was charged twice. Please fix this ASAP.",
///     new Questions
///     {
///         ["billing"] = Question.Noul("Is this ticket about billing?"),
///         ["urgency"] = Question.Score("How urgent is this ticket?", "can wait", "this week", "today"),
///     });
/// Console.WriteLine(result.Noul("billing").Noul);
/// </code>
/// </example>
public sealed class JevClient : IJevClient, IDisposable
{
    private static readonly string SdkVersion =
        typeof(JevClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly HashSet<string> ProtectedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Accept", "User-Agent", "Content-Type", "X-TypeSafe-Retry-Count",
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ILogger _logger;
    private readonly string _apiKey;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly IReadOnlyDictionary<string, string> _defaultHeaders;
    private long _requestCount;

    /// <summary>Create a client.</summary>
    /// <param name="options">Configuration; unset values fall back to environment variables, then defaults.</param>
    /// <param name="httpClient">
    /// Optional <see cref="HttpClient"/> for custom transport (proxies, HTTP/2, tests). The client does
    /// not dispose it. Its own <see cref="HttpClient.Timeout"/> still applies on top of <see cref="Timeout"/>.
    /// </param>
    /// <param name="logger">Optional logger. <c>Information</c> logs one line per attempt; <c>Debug</c> adds
    /// headers (credentials redacted) and bodies (not redacted).</param>
    /// <exception cref="JevException">The API key is missing or malformed, or the configuration is invalid.</exception>
    public JevClient(JevClientOptions? options = null, HttpClient? httpClient = null, ILogger? logger = null)
    {
        options ??= new JevClientOptions();

        _apiKey = ResolveApiKey(options.ApiKey);
        BaseUrl = (FromCodeOrEnv(options.BaseUrl, "TYPESAFE_BASE_URL") ?? JevClientOptions.DefaultBaseUrl).TrimEnd('/');
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
            throw new JevException($"Base URL \"{BaseUrl}\" is not an absolute URL.");
        DefaultModel = FromCodeOrEnv(options.DefaultModel, "TYPESAFE_DEFAULT_MODEL") ?? JevClientOptions.DefaultModelName;
        Timeout = ValidateTimeout(options.Timeout);
        options.Retry.Validate();
        Retry = options.Retry;
        _defaultHeaders = new Dictionary<string, string>(options.DefaultHeaders, StringComparer.OrdinalIgnoreCase);
        _serializerOptions = options.SerializerOptions ?? new JsonSerializerOptions();
        _logger = logger ?? NullLogger.Instance;

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>API root, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Model used when a request does not set one.</summary>
    public string DefaultModel { get; }

    /// <summary>Timeout for each attempt.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Default retry policy.</summary>
    public RetryPolicy Retry { get; }

    /// <inheritdoc />
    public Task<SystemOneResponse> SystemOneAsync(
        object? state,
        IReadOnlyDictionary<string, Question> questions,
        string? model = null,
        CancellationToken cancellationToken = default) =>
        SystemOneAsync(new SystemOneRequest { State = state, Questions = questions, Model = model }, null, cancellationToken);

    /// <inheritdoc />
    public async Task<SystemOneResponse> SystemOneAsync(
        SystemOneRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateQuestions(request.Questions);

        var body = new JsonObject
        {
            ["state"] = ToNode(request.State),
            ["model"] = request.Model ?? DefaultModel,
            ["questions"] = BuildQuestions(request.Questions),
        };
        if (request.ExtraBody is not null)
            foreach (var (key, value) in request.ExtraBody)
                body[key] = ToNode(value);

        var response = await SendAsync(HttpMethod.Post, "/v1/systemone", body.ToJsonString(), options, cancellationToken).ConfigureAwait(false);
        return ParseSystemOne(response);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelCard>> ListModelsAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, "/v1/models", null, options, cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            return doc.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => new ModelCard(
                    m.GetProperty("name").GetString()!,
                    m.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    m.TryGetProperty("release_date", out var r) ? r.GetString() ?? "" : ""))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new JevException("Unexpected response shape from GET /v1/models; expected { \"models\": [...] }.", ex);
        }
    }

    /// <summary>Dispose the underlying <see cref="HttpClient"/> if this client created it.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    // -----------------------------------------------------------------------
    // Request building
    // -----------------------------------------------------------------------

    private static void ValidateQuestions(IReadOnlyDictionary<string, Question>? questions)
    {
        if (questions is null || questions.Count == 0)
            throw new JevException("At least one question is required.");
        foreach (var (id, question) in questions)
        {
            switch (question)
            {
                case null:
                    throw new JevException($"Question \"{id}\" is null.");
                case ScoreQuestion { Levels: null or { Count: < 2 } } s:
                    throw new JevException(
                        $"Score question \"{id}\" has {s.Levels?.Count ?? 0} levels; at least two are required.");
                case ChoiceQuestion { Criteria: null or { Count: 0 } }:
                    throw new JevException($"Choice question \"{id}\" has no options.");
            }
        }
    }

    private JsonObject BuildQuestions(IReadOnlyDictionary<string, Question> questions)
    {
        var result = new JsonObject();
        foreach (var (id, question) in questions)
        {
            var node = new JsonObject
            {
                ["type"] = question.Type,
                ["instructions"] = ToNode(question.Instructions),
            };
            switch (question)
            {
                case NoulQuestion n when n.WhenTrue is not null || n.WhenFalse is not null:
                    var criteria = new JsonObject();
                    if (n.WhenTrue is not null) criteria["true"] = ToNode(n.WhenTrue);
                    if (n.WhenFalse is not null) criteria["false"] = ToNode(n.WhenFalse);
                    node["criteria"] = criteria;
                    break;
                case ChoiceQuestion c:
                    var options = new JsonObject();
                    foreach (var (option, description) in c.Criteria) options[option] = ToNode(description);
                    node["criteria"] = options;
                    break;
                case ScoreQuestion s:
                    node["criteria"] = new JsonArray(s.Levels.Select(ToNode).ToArray());
                    break;
            }
            result[id] = node;
        }
        return result;
    }

    private JsonNode? ToNode(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), _serializerOptions);

    // -----------------------------------------------------------------------
    // Transport
    // -----------------------------------------------------------------------

    private sealed record RawResponse(string Body, HttpResponseHeaders Headers);

    private async Task<RawResponse> SendAsync(
        HttpMethod method, string path, string? body, RequestOptions? options, CancellationToken cancellationToken)
    {
        var timeout = options?.Timeout is { } t ? ValidateTimeout(t) : Timeout;
        var retry = options?.Retry ?? Retry;
        retry.Validate();
        var headers = new Dictionary<string, string>(_defaultHeaders, StringComparer.OrdinalIgnoreCase);
        if (options?.Headers is not null)
            foreach (var (k, v) in options.Headers) headers[k] = v;

        var url = BaseUrl + path;
        var tag = $"#{Interlocked.Increment(ref _requestCount)} {method} {path}";

        for (var attempt = 0; ; attempt++)
        {
            var retriesLeft = retry.MaxRetries - attempt;
            using var message = BuildMessage(method, url, body, headers, attempt);
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("{Tag} -> {Url} headers={Headers} body={Body}", tag, url, Redact(message), body);

            var started = Stopwatch.GetTimestamp();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(timeout);

            JevConnectionException failure;
            try
            {
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, attemptCts.Token)
                    .ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(attemptCts.Token).ConfigureAwait(false);
                var requestId = RequestIds.From(response.Headers);
                _logger.LogInformation("{Tag} <- {Status} in {Elapsed}ms{RequestId}", tag, (int)response.StatusCode,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, requestId is null ? "" : $" (request {requestId})");
                _logger.LogDebug("{Tag} <- body {Body}", tag, text);

                if (response.IsSuccessStatusCode) return new RawResponse(text, response.Headers);

                var error = JevApiException.FromResponse(response.StatusCode, text.Length == 0 ? null : text, response.Headers);
                if (retriesLeft <= 0 || !retry.RetryStatusCodes.Contains((int)response.StatusCode)) throw error;
                await BackOffAsync(tag, attempt, retriesLeft, ((int)response.StatusCode).ToString(), response.Headers, retry, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("{Tag} cancelled by caller", tag);
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Our per-attempt timer, or the HttpClient's own Timeout, fired.
                _logger.LogInformation("{Tag} timed out after {Elapsed}ms", tag, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                failure = new JevTimeoutException(timeout, ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                _logger.LogInformation(ex, "{Tag} connection error", tag);
                failure = new JevConnectionException($"Connection error: {ex.Message}", ex);
            }

            var retryable = failure is JevTimeoutException ? retry.RetryTimeouts : retry.RetryConnectionErrors;
            if (retriesLeft <= 0 || !retryable) throw failure;
            await BackOffAsync(tag, attempt, retriesLeft, failure.Message, null, retry, cancellationToken).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage BuildMessage(HttpMethod method, string url, string? body, Dictionary<string, string> headers, int attempt)
    {
        var message = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers)
            if (!ProtectedHeaders.Contains(name))
                message.Headers.TryAddWithoutValidation(name, value);

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jev.DotNet", SdkVersion));
        if (attempt > 0) message.Headers.Add("X-TypeSafe-Retry-Count", attempt.ToString());
        if (body is not null) message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return message;
    }

    private async Task BackOffAsync(string tag, int attempt, int retriesLeft, string reason,
        HttpResponseHeaders? headers, RetryPolicy retry, CancellationToken cancellationToken)
    {
        var delay = Backoff.Delay(attempt, headers, retry, Random.Shared.NextDouble);
        _logger.LogInformation("{Tag} retrying in {Delay}ms (retry {N}/{Total}) after {Reason}",
            tag, (long)delay.TotalMilliseconds, attempt + 1, attempt + retriesLeft, reason);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static string Redact(HttpRequestMessage message) =>
        string.Join(", ", message.Headers.Select(h =>
        {
            var name = h.Key.ToLowerInvariant();
            var secret = name is "authorization" or "cookie" or "x-api-key" or "api-key"
                || name.Contains("token") || name.Contains("secret");
            return $"{h.Key}: {(secret ? "[redacted]" : string.Join(",", h.Value))}";
        }));

    // -----------------------------------------------------------------------
    // Response parsing
    // -----------------------------------------------------------------------

    private SystemOneResponse ParseSystemOne(RawResponse response)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new JevException("The API returned a response that is not valid JSON.", ex);
        }

        var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        if (root.TryGetProperty("answers", out var answersElement))
        {
            foreach (var p in answersElement.EnumerateObject())
            {
                var answer = Answer.Parse(p.Value);
                if (answer is UnknownAnswer)
                    _logger.LogWarning("Answer \"{Id}\" has unrecognized type \"{Type}\"; read it from Answer.Raw or upgrade Jev.DotNet.",
                        p.Name, answer.Type);
                answers[p.Name] = answer;
            }
        }

        var usage = root.TryGetProperty("usage", out var u)
            ? new Usage(
                u.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0,
                u.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0)
            : new Usage(0, 0);
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";

        return new SystemOneResponse(model, answers, usage, RequestIds.From(response.Headers), root);
    }

    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    private static string? FromCodeOrEnv(string? fromCode, string envVar)
    {
        if (fromCode is not null) return fromCode;
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
    }

    private static string ResolveApiKey(string? fromCode)
    {
        var key = FromCodeOrEnv(fromCode, "TYPESAFE_API_KEY")?.Trim()
            ?? throw new JevException(
                "No API key was provided. Set JevClientOptions.ApiKey or the TYPESAFE_API_KEY environment variable. " +
                "Create a key at https://console.typesafe.ai/.");
        if (key.Length == 0)
            throw new JevException("The API key is empty.");
        if (key.Any(c => c > 127 || char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new JevException("The API key contains whitespace, control, or non-ASCII characters. Check for copy-paste errors.");
        return key;
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero || timeout == System.Threading.Timeout.InfiniteTimeSpan
            ? timeout
            : throw new JevException($"Timeout must be positive, got {timeout}.");
}
