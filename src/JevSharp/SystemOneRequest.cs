namespace JevSharp;

/// <summary>A request to evaluate <see cref="State"/> against a set of <see cref="Questions"/>.</summary>
public sealed class SystemOneRequest
{
    /// <summary>
    /// The content to evaluate: a string, or any JSON-serializable object or array (records,
    /// anonymous types, <c>JsonNode</c>). Prefer named fields when the context has several parts.
    /// </summary>
    public object? State { get; set; }

    /// <summary>
    /// Questions keyed by id. Questions over the same state run in parallel and cannot see each
    /// other's answers.
    /// </summary>
    public required IReadOnlyDictionary<string, Question> Questions { get; set; }

    /// <summary>Model override for this request; defaults to the client's model.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Additional top-level request fields, sent as-is. A forward-compatibility escape hatch for
    /// API features this SDK version does not model yet.
    /// </summary>
    public IDictionary<string, object?>? ExtraBody { get; set; }
}
