using System.Globalization;
using System.Text.Json;

namespace JevSharp;

/// <summary>An answer to one question. Cast or use the typed accessors on <see cref="SystemOneResponse"/>.</summary>
public abstract record Answer
{
    /// <summary>The wire type, matching the question's type.</summary>
    public required string Type { get; init; }

    /// <summary>The answer exactly as the API returned it.</summary>
    public required JsonElement Raw { get; init; }

    internal static Answer Parse(JsonElement element)
    {
        var raw = element.Clone();
        var type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        return type switch
        {
            "noul" => new NoulAnswer { Type = type, Raw = raw, Noul = element.GetProperty("noul").GetDouble() },
            "choice" => new ChoiceAnswer
            {
                Type = type,
                Raw = raw,
                Choice = element.GetProperty("choice").GetString()!,
                Confidence = element.GetProperty("confidence").GetDouble(),
                Probabilities = ReadProbabilities(element, key => key),
            },
            "score" => new ScoreAnswer
            {
                Type = type,
                Raw = raw,
                Score = element.GetProperty("score").GetDouble(),
                Confidence = element.GetProperty("confidence").GetDouble(),
                Probabilities = ReadProbabilities(element, ParseLevel),
                Legend = ReadLegend(element),
            },
            _ => new UnknownAnswer { Type = type, Raw = raw },
        };
    }

    private static int ParseLevel(string key) => int.Parse(key, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<TKey, double> ReadProbabilities<TKey>(JsonElement element, Func<string, TKey> key)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, double>();
        foreach (var p in element.GetProperty("probabilities").EnumerateObject())
            result[key(p.Name)] = p.Value.GetDouble();
        return result;
    }

    private static IReadOnlyDictionary<int, string> ReadLegend(JsonElement element)
    {
        var result = new SortedDictionary<int, string>();
        if (!element.TryGetProperty("legend", out var legend) || legend.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var p in legend.EnumerateObject())
            result[ParseLevel(p.Name)] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        return result;
    }
}

/// <summary>The answer to a yes/no question.</summary>
public sealed record NoulAnswer : Answer
{
    /// <summary>Probability that the answer is yes, from 0 to 1. Near 0.5 means yes and no are similarly likely.</summary>
    public required double Noul { get; init; }
}

/// <summary>The answer to a choice question.</summary>
public sealed record ChoiceAnswer : Answer
{
    /// <summary>The highest-probability option.</summary>
    public required string Choice { get; init; }

    /// <summary>How concentrated the distribution is, from 0 to 1. Not a guarantee of correctness.</summary>
    public required double Confidence { get; init; }

    /// <summary>Every option mapped to its probability; values sum to 1.</summary>
    public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

    /// <summary>The chosen option as a member of <typeparamref name="TEnum"/> (see <see cref="Question.Choice{TEnum}"/>).</summary>
    /// <exception cref="JevException">The chosen option is not a member of <typeparamref name="TEnum"/>.</exception>
    public TEnum As<TEnum>() where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(Choice, ignoreCase: false, out var value) && Enum.IsDefined(value)
            ? value
            : throw new JevException($"Choice \"{Choice}\" is not a member of {typeof(TEnum).Name}.");

    /// <summary>The probability of <paramref name="option"/>, or 0 when it is not present.</summary>
    public double ProbabilityOf(string option) => Probabilities.TryGetValue(option, out var p) ? p : 0;

    /// <summary>The probability of an enum option (see <see cref="Question.Choice{TEnum}"/>).</summary>
    public double ProbabilityOf<TEnum>(TEnum option) where TEnum : struct, Enum => ProbabilityOf(option.ToString());
}

/// <summary>The answer to a score question.</summary>
public sealed record ScoreAnswer : Answer
{
    /// <summary>The probability-weighted level; may fall between levels (e.g. 1.05).</summary>
    public required double Score { get; init; }

    /// <summary>How concentrated the distribution is, from 0 to 1. Not a guarantee of correctness.</summary>
    public required double Confidence { get; init; }

    /// <summary>Each level index mapped to its probability; values sum to 1.</summary>
    public required IReadOnlyDictionary<int, double> Probabilities { get; init; }

    /// <summary>Each level index mapped back to its description.</summary>
    public required IReadOnlyDictionary<int, string> Legend { get; init; }

    /// <summary>The single most likely level.</summary>
    public int MostLikelyLevel => Probabilities.MaxBy(p => p.Value).Key;
}

/// <summary>An answer type this version of the SDK does not recognize. Inspect <see cref="Answer.Raw"/>.</summary>
public sealed record UnknownAnswer : Answer;

/// <summary>Token usage for a request. Only input tokens are billed.</summary>
public sealed record Usage(int InputTokens, int OutputTokens);

/// <summary>The result of <see cref="IJevClient.SystemOneAsync(SystemOneRequest, RequestOptions?, CancellationToken)"/>.</summary>
public sealed class SystemOneResponse
{
    internal SystemOneResponse(string model, IReadOnlyDictionary<string, Answer> answers, Usage usage, string? requestId, JsonElement raw)
    {
        Model = model;
        Answers = answers;
        Usage = usage;
        RequestId = requestId;
        Raw = raw;
    }

    /// <summary>The versioned model that answered, e.g. <c>jev-1.13.0</c>. Log it alongside results.</summary>
    public string Model { get; }

    /// <summary>Answers keyed by the ids you gave the questions.</summary>
    public IReadOnlyDictionary<string, Answer> Answers { get; }

    /// <summary>Token usage for the request.</summary>
    public Usage Usage { get; }

    /// <summary>The <c>x-typesafe-request-id</c> header, when present. Include it in support requests.</summary>
    public string? RequestId { get; }

    /// <summary>The full response body.</summary>
    public JsonElement Raw { get; }

    /// <summary>The yes/no answer for <paramref name="id"/>.</summary>
    public NoulAnswer Noul(string id) => Get<NoulAnswer>(id);

    /// <summary>The choice answer for <paramref name="id"/>.</summary>
    public ChoiceAnswer Choice(string id) => Get<ChoiceAnswer>(id);

    /// <summary>The score answer for <paramref name="id"/>.</summary>
    public ScoreAnswer Score(string id) => Get<ScoreAnswer>(id);

    /// <summary>The answer for <paramref name="id"/> as <typeparamref name="T"/>.</summary>
    /// <exception cref="JevException">There is no answer with that id, or it has a different type.</exception>
    public T Get<T>(string id) where T : Answer
    {
        if (!Answers.TryGetValue(id, out var answer))
            throw new JevException($"No answer with id \"{id}\". Answers: {string.Join(", ", Answers.Keys)}.");
        return answer as T
            ?? throw new JevException($"Answer \"{id}\" is a {answer.Type} answer, not {typeof(T).Name}.");
    }
}

/// <summary>A model or alias the account can use, from <c>GET /v1/models</c>.</summary>
public sealed record ModelCard(string Name, string Description, string ReleaseDate);
