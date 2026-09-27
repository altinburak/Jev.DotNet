using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Jev.DotNet.Tests;

public class JevClientTests
{
    private const string MixedResponse = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "billing": { "type": "noul", "noul": 0.95 },
            "tone": { "type": "choice", "choice": "frustrated",
                      "probabilities": { "calm": 0.05, "frustrated": 0.85, "angry": 0.10 }, "confidence": 0.7 },
            "urgency": { "type": "score", "score": 1.05,
                         "legend": { "0": "can wait", "1": "this week", "2": "today" },
                         "probabilities": { "0": 0.0, "1": 0.95, "2": 0.05 }, "confidence": 0.92 }
          },
          "usage": { "input_tokens": 296, "output_tokens": 20 }
        }
        """;

    private static (JevClient Client, StubHandler Handler) Create(RetryPolicy? retry = null, params Func<HttpResponseMessage>[] responses)
    {
        var handler = new StubHandler(responses);
        var client = new JevClient(
            new JevClientOptions { ApiKey = "test-key", BaseUrl = "https://example.test/", Retry = retry ?? RetryPolicy.Default },
            new HttpClient(handler));
        return (client, handler);
    }

    private static Func<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK, Action<HttpResponseMessage>? configure = null) => () =>
    {
        var res = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        configure?.Invoke(res);
        return res;
    };

    [Fact]
    public async Task Sends_expected_request_shape()
    {
        var (client, handler) = Create(null, Json(MixedResponse));

        await client.SystemOneAsync(
            new { ticket = new { body = "I was charged twice." } },
            new Questions
            {
                ["billing"] = Question.Noul("Is `ticket.body` about billing?", whenTrue: "Payments or invoices"),
                ["tone"] = Question.Choice("What is the tone?", "calm", "frustrated", "angry"),
                ["urgency"] = Question.Score("How urgent?", "can wait", "this week", "today"),
                ["plain"] = Question.Noul("Plain yes/no?"),
            });

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://example.test/v1/systemone", req.Uri);
        Assert.Equal("Bearer test-key", req.Headers["Authorization"]);
        Assert.False(req.Headers.ContainsKey("X-TypeSafe-Retry-Count"));

        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("jev-latest", (string?)body["model"]);
        Assert.Equal("I was charged twice.", (string?)body["state"]!["ticket"]!["body"]);
        Assert.Equal("noul", (string?)body["questions"]!["billing"]!["type"]);
        Assert.Equal("Payments or invoices", (string?)body["questions"]!["billing"]!["criteria"]!["true"]);
        Assert.Null(body["questions"]!["billing"]!["criteria"]!["false"]);
        Assert.Null(body["questions"]!["plain"]!["criteria"]);
        Assert.Equal(3, body["questions"]!["tone"]!["criteria"]!.AsObject().Count);
        Assert.Equal("today", (string?)body["questions"]!["urgency"]!["criteria"]![2]);
    }

    [Fact]
    public async Task Parses_typed_answers()
    {
        var (client, _) = Create(null, Json(MixedResponse, configure: r => r.Headers.Add("x-typesafe-request-id", "req_123")));

        var result = await client.SystemOneAsync("text", new Questions { ["billing"] = Question.Noul("?") });

        Assert.Equal("jev-1.13.0", result.Model);
        Assert.Equal("req_123", result.RequestId);
        Assert.Equal(new Usage(296, 20), result.Usage);
        Assert.Equal(0.95, result.Noul("billing").Noul);

        var tone = result.Choice("tone");
        Assert.Equal("frustrated", tone.Choice);
        Assert.Equal(0.85, tone.ProbabilityOf("frustrated"));
        Assert.Equal(Tone.frustrated, tone.As<Tone>());

        var urgency = result.Score("urgency");
        Assert.Equal(1.05, urgency.Score);
        Assert.Equal("today", urgency.Legend[2]);
        Assert.Equal(1, urgency.MostLikelyLevel);

        var ex = Assert.Throws<JevException>(() => result.Score("billing"));
        Assert.Contains("noul", ex.Message);
    }

    [Fact]
    public async Task Unknown_answer_types_are_preserved()
    {
        var (client, _) = Create(null, Json("""{"model":"m","answers":{"x":{"type":"future","value":1}},"usage":{"input_tokens":1,"output_tokens":1}}"""));
        var result = await client.SystemOneAsync("s", new Questions { ["x"] = Question.Noul("?") });
        var answer = Assert.IsType<UnknownAnswer>(result.Answers["x"]);
        Assert.Equal(1, answer.Raw.GetProperty("value").GetInt32());
    }

    private enum Tone
    {
        calm,
        [Description("Annoyed but polite")] frustrated,
        angry,
    }

    [Fact]
    public void Enum_choice_uses_member_names_and_descriptions()
    {
        var q = Question.Choice<Tone>("What is the tone?");
        Assert.Equal(["calm", "frustrated", "angry"], q.Criteria.Keys);
        Assert.Equal("Annoyed but polite", q.Criteria["frustrated"]);
        Assert.Null(q.Criteria["calm"]);
    }

    [Fact]
    public async Task Retries_rate_limits_then_succeeds()
    {
        var (client, handler) = Create(null,
            Json("""{"error":"slow down"}""", HttpStatusCode.TooManyRequests, r => r.Headers.Add("retry-after-ms", "0")),
            Json("""{"error":"overloaded"}""", (HttpStatusCode)529, r => r.Headers.Add("retry-after-ms", "0")),
            Json(MixedResponse));

        var result = await client.SystemOneAsync("s", new Questions { ["billing"] = Question.Noul("?") });

        Assert.Equal(0.95, result.Noul("billing").Noul);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("2", handler.Requests[2].Headers["X-TypeSafe-Retry-Count"]);
    }

    [Fact]
    public async Task Validation_errors_are_not_retried_and_describe_fields()
    {
        var (client, handler) = Create(null, Json("""
            {"detail":[{"loc":["body","questions","x","criteria"],"msg":"field required","type":"missing"}]}
            """, HttpStatusCode.UnprocessableEntity));

        var ex = await Assert.ThrowsAsync<JevValidationException>(() =>
            client.SystemOneAsync("s", new Questions { ["x"] = Question.Noul("?") }));

        Assert.Single(handler.Requests);
        Assert.Equal("422 questions.x.criteria: field required", ex.Message);
    }

    [Fact]
    public async Task Rate_limit_exhausted_exposes_retry_after()
    {
        var (client, handler) = Create(RetryPolicy.None,
            Json("""{"error":"slow down"}""", HttpStatusCode.TooManyRequests, r => r.Headers.Add("Retry-After", "7")));

        var ex = await Assert.ThrowsAsync<JevRateLimitException>(() =>
            client.SystemOneAsync("s", new Questions { ["x"] = Question.Noul("?") }));

        Assert.Single(handler.Requests);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    [Fact]
    public async Task Timeouts_retry_then_throw()
    {
        var handler = new StubHandler(delay: TimeSpan.FromSeconds(5));
        var client = new JevClient(
            new JevClientOptions
            {
                ApiKey = "k",
                Timeout = TimeSpan.FromMilliseconds(50),
                Retry = new RetryPolicy { MaxRetries = 1, BackoffInitial = TimeSpan.Zero },
            },
            new HttpClient(handler));

        await Assert.ThrowsAsync<JevTimeoutException>(() =>
            client.SystemOneAsync("s", new Questions { ["x"] = Question.Noul("?") }));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_timeout()
    {
        var handler = new StubHandler(delay: TimeSpan.FromSeconds(5));
        var client = new JevClient(new JevClientOptions { ApiKey = "k" }, new HttpClient(handler));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SystemOneAsync("s", new Questions { ["x"] = Question.Noul("?") }, cancellationToken: cts.Token));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("ünïcode")]
    public void Rejects_malformed_api_keys(string key) =>
        Assert.Throws<JevException>(() => new JevClient(new JevClientOptions { ApiKey = key }));

    [Fact]
    public void Trims_api_key_whitespace() =>
        _ = new JevClient(new JevClientOptions { ApiKey = "  key\n" });

    [Fact]
    public async Task Rejects_invalid_questions_before_sending()
    {
        var (client, handler) = Create();
        await Assert.ThrowsAsync<JevException>(() => client.SystemOneAsync("s", new Questions()));
        await Assert.ThrowsAsync<JevException>(() =>
            client.SystemOneAsync("s", new Questions { ["x"] = Question.Score("?", "only one") }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Extra_body_and_model_override_are_sent()
    {
        var (client, handler) = Create(null, Json(MixedResponse));
        await client.SystemOneAsync(new SystemOneRequest
        {
            State = "s",
            Model = "jev-1.13.0",
            Questions = new Questions { ["x"] = Question.Noul("?") },
            ExtraBody = new Dictionary<string, object?> { ["beam_width"] = 4 },
        });
        var body = JsonNode.Parse(handler.Requests[0].Body)!;
        Assert.Equal("jev-1.13.0", (string?)body["model"]);
        Assert.Equal(4, (int?)body["beam_width"]);
    }

    [Fact]
    public async Task Lists_models()
    {
        var (client, handler) = Create(null, Json("""{"models":[{"name":"jev-latest","description":"Latest","release_date":"2026-01-01"}]}"""));
        var models = await client.ListModelsAsync();
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(new ModelCard("jev-latest", "Latest", "2026-01-01"), Assert.Single(models));
    }

    [Fact]
    public void Registers_with_dependency_injection()
    {
        var services = new ServiceCollection();
        services.AddJevClient(o => o.ApiKey = "di-key");
        using var provider = services.BuildServiceProvider();
        Assert.IsType<JevClient>(provider.GetRequiredService<IJevClient>());
    }
}

internal sealed class StubHandler(Func<HttpResponseMessage>[]? responses = null, TimeSpan? delay = null) : HttpMessageHandler
{
    public sealed record Captured(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string Body);

    private int _next;
    public List<Captured> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Captured(request.Method, request.RequestUri!.ToString(), headers, body));
        if (delay is { } d) await Task.Delay(d, cancellationToken);
        return responses![_next++]();
    }
}
