# Jev.DotNet

An unofficial .NET client for [TypeSafe](https://typesafe.ai)'s **Jev** model.

Jev is a System One model. You give it state (text or JSON) and typed questions, and it
returns probabilities instead of generated text. Jev.DotNet wraps the
[HTTP API](https://docs.typesafe.ai/api) with typed questions and answers, retries,
timeouts, logging, and `IHttpClientFactory` / DI support.

> This is a community package. It is not published or supported by TypeSafe.

## Install

```sh
dotnet add package Jev.DotNet
```

Targets .NET 8 and later. Create an API key at [console.typesafe.ai](https://console.typesafe.ai/)
and set `TYPESAFE_API_KEY`.

## Quickstart

```csharp
using Jev.DotNet;

using var client = new JevClient(); // reads TYPESAFE_API_KEY

var result = await client.SystemOneAsync(
    state: new { ticket = new { body = "I was charged twice. Please fix this ASAP." } },
    questions: new Questions
    {
        ["billing"] = Question.Noul("Is `ticket.body` about billing?"),
        ["tone"]    = Question.Choice("What is the customer's tone?", "calm", "frustrated", "angry"),
        ["urgency"] = Question.Score("How urgent is this ticket?", "can wait", "this week", "today"),
    });

Console.WriteLine(result.Noul("billing").Noul);      // 0.97, the probability of yes
Console.WriteLine(result.Choice("tone").Choice);     // "frustrated"
Console.WriteLine(result.Score("urgency").Score);    // 1.8, a weighted level from 0 to 2
```

All questions in one request are evaluated against the same state in parallel. Answers come
back under the ids you chose. The ids themselves are never sent to the model, so each question
has to carry its full meaning.

## Questions

| Factory | Answer | Use when |
| --- | --- | --- |
| `Question.Noul(instructions, whenTrue?, whenFalse?)` | `NoulAnswer.Noul`: P(yes), from 0 to 1 | A condition holds or it doesn't. For multi-label, ask one Noul per label. |
| `Question.Choice(instructions, "a", "b", ...)` | `ChoiceAnswer.Choice`, `.Probabilities`, `.Confidence` | Pick one of up to 255 options. |
| `Question.Choice<TEnum>(instructions)` | `.As<TEnum>()` | Options are enum members; `[Description]` becomes the rubric. |
| `Question.Score(instructions, level0, level1, ...)` | `ScoreAnswer.Score`, `.Legend`, `.Probabilities` | Rate on 2–10 ordered levels. The score can land between levels. |

Instructions, criteria, and state can be strings or any JSON-serializable object, such as an
anonymous type, a record, or a `JsonNode`. Structure helps when a question references data:

```csharp
Question.Noul(new
{
    potential_duplicate = new { name = "John Smith", location = "Oakland, California" },
    question = "Is the resume for the same person as `potential_duplicate`?",
});
```

Property names are sent exactly as declared, so backticked paths must match your member names.
To use a naming policy, set `JevClientOptions.SerializerOptions`.

### Typed choices with enums

```csharp
enum Team
{
    [Description("Payments, invoicing, refunds")] Billing,
    [Description("Bugs, outages, integrations")]  Technical,
}

var result = await client.SystemOneAsync(text, new Questions { ["team"] = Question.Choice<Team>("Which team should handle this?") });
Team team = result.Choice("team").As<Team>();
```

## ASP.NET Core / dependency injection

```csharp
builder.Services.AddJevClient(o => o.ApiKey = builder.Configuration["TypeSafe:ApiKey"]);

app.MapPost("/triage", async (IJevClient jev, Ticket t, CancellationToken ct) =>
{
    var r = await jev.SystemOneAsync(t, new Questions { ["urgent"] = Question.Noul("Is this urgent?") }, cancellationToken: ct);
    return r.Noul("urgent").Noul;
});
```

`AddJevClient` returns the `IHttpClientBuilder`, so you can chain handlers, proxies, or resilience
policies. Keep the API key on the server.

## Configuration

| Option | Env var | Default |
| --- | --- | --- |
| `ApiKey` | `TYPESAFE_API_KEY` | required |
| `BaseUrl` | `TYPESAFE_BASE_URL` | `https://api.typesafe.ai` |
| `DefaultModel` | `TYPESAFE_DEFAULT_MODEL` | `jev-latest` |
| `Timeout` (per attempt) | | 10 s |
| `Retry` | | 2 retries on 408/429/5xx/timeouts/connection errors, 0.5 s → 5 s backoff, honors `Retry-After` |

Any per-call option can be overridden with `RequestOptions`:

```csharp
await client.SystemOneAsync(new SystemOneRequest { State = s, Questions = q, Model = "jev-1.13.0" },
    new RequestOptions { Timeout = TimeSpan.FromSeconds(30), Retry = RetryPolicy.None });
```

`jev-latest` is an alias that moves with new releases. If you've tuned thresholds against one
version, pin it by its versioned id (`result.Model` shows the version that answered).

Gateways that implement the TypeSafe API also work. Point `BaseUrl` at the gateway and use its key
and model id, for example OpenRouter (`https://openrouter.ai/api`, `~typesafe/jev-latest`).

## Errors

| Exception | When |
| --- | --- |
| `JevException` | Base class. Also thrown for invalid config or questions, before anything is sent. |
| `JevApiException` → `JevAuthenticationException` (401), `JevValidationException` (422), `JevRateLimitException` (429, `.RetryAfter`), `JevServerException` (5xx), … | The API returned an error after retries. `.StatusCode`, `.Body`, and `.RequestId` are set. |
| `JevConnectionException` / `JevTimeoutException` | Network failure or timeout after retries. |
| `OperationCanceledException` | Your `CancellationToken` fired. |

## Logging

Pass an `ILogger` to the constructor. DI does this for you. `Information` writes one line per
attempt, with status, latency, and request id. `Debug` also writes headers, with credentials
redacted, and bodies, which are **not** redacted.

## Forward compatibility

- `SystemOneRequest.ExtraBody` sends top-level fields that this SDK doesn't model yet.
- Answer types this SDK doesn't recognize come back as `UnknownAnswer`, with the JSON in `.Raw`.
  `SystemOneResponse.Raw` holds the full response.

## Build

```sh
dotnet test
dotnet pack src/Jev.DotNet -c Release -o artifacts
dotnet nuget push artifacts/Jev.DotNet.*.nupkg --api-key <NUGET_KEY> --source https://api.nuget.org/v3/index.json
```

To learn how to design good questions, see the [TypeSafe docs](https://docs.typesafe.ai/),
starting with [How to build with System One](https://docs.typesafe.ai/concepts/how-to-build-with-system-one)
and [Confidence](https://docs.typesafe.ai/confidence).
