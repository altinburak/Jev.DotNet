using System.ComponentModel;
using System.Reflection;

namespace Jev.DotNet;

/// <summary>
/// A typed question for Jev. Create one with <see cref="Noul"/>, <see cref="Choice(object?, string[])"/>,
/// or <see cref="Score(object?, object?[])"/>.
/// </summary>
/// <remarks>
/// <c>Instructions</c> and criteria descriptions may be a string, or any JSON-serializable
/// object or array (anonymous types, records, <c>JsonNode</c>). Use objects to separate the
/// question from reference data, and point at fields with backticks, e.g. <c>`ticket.body`</c>.
/// </remarks>
public abstract record Question
{
    private protected Question(object? instructions) => Instructions = instructions;

    /// <summary>The wire type: <c>noul</c>, <c>choice</c>, or <c>score</c>.</summary>
    public abstract string Type { get; }

    /// <summary>The question as text, a JSON-serializable object or array, or <c>null</c>.</summary>
    public object? Instructions { get; init; }

    /// <summary>Create a yes/no question. The answer is the probability of yes.</summary>
    /// <param name="instructions">The yes/no question.</param>
    /// <param name="whenTrue">Optional description of what a yes means.</param>
    /// <param name="whenFalse">Optional description of what a no means.</param>
    public static NoulQuestion Noul(object? instructions, object? whenTrue = null, object? whenFalse = null) =>
        new(instructions, whenTrue, whenFalse);

    /// <summary>Create a question that picks one of the given options, with no descriptions.</summary>
    public static ChoiceQuestion Choice(object? instructions, params string[] options) =>
        new(instructions, options.ToDictionary(o => o, _ => (object?)null));

    /// <summary>Create a question that picks one option; each option maps to a description (or <c>null</c>).</summary>
    public static ChoiceQuestion Choice(object? instructions, IReadOnlyDictionary<string, object?> criteria) =>
        new(instructions, criteria);

    /// <summary>
    /// Create a choice question whose options are the members of <typeparamref name="TEnum"/>.
    /// A member's <see cref="DescriptionAttribute"/>, if present, becomes its description.
    /// Read the answer back with <see cref="ChoiceAnswer.As{TEnum}"/>.
    /// </summary>
    public static ChoiceQuestion Choice<TEnum>(object? instructions) where TEnum : struct, Enum
    {
        var criteria = new Dictionary<string, object?>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
            criteria[field.Name] = field.GetCustomAttribute<DescriptionAttribute>()?.Description;
        return new ChoiceQuestion(instructions, criteria);
    }

    /// <summary>Create a question that rates the state on ordered levels, lowest first (2 to 10 levels).</summary>
    public static ScoreQuestion Score(object? instructions, params object?[] levels) =>
        new(instructions, levels);

    /// <summary>Create a question that rates the state on ordered levels, lowest first (2 to 10 levels).</summary>
    public static ScoreQuestion Score(object? instructions, IReadOnlyList<object?> levels) =>
        new(instructions, levels);
}

/// <summary>A yes/no question. See <see cref="Question.Noul"/>.</summary>
public sealed record NoulQuestion(object? Instructions, object? WhenTrue = null, object? WhenFalse = null)
    : Question(Instructions)
{
    /// <inheritdoc />
    public override string Type => "noul";
}

/// <summary>A question that selects one option. See <see cref="Question.Choice(object?, string[])"/>.</summary>
public sealed record ChoiceQuestion(object? Instructions, IReadOnlyDictionary<string, object?> Criteria)
    : Question(Instructions)
{
    /// <inheritdoc />
    public override string Type => "choice";
}

/// <summary>A question that rates the state on ordered levels. See <see cref="Question.Score(object?, object?[])"/>.</summary>
public sealed record ScoreQuestion(object? Instructions, IReadOnlyList<object?> Levels)
    : Question(Instructions)
{
    /// <inheritdoc />
    public override string Type => "score";
}

/// <summary>Questions keyed by the ids you use to read their answers. Ids are not sent to the model.</summary>
/// <example>
/// <code>
/// var questions = new Questions
/// {
///     ["billing"] = Question.Noul("Is this ticket about billing?"),
///     ["tone"] = Question.Choice("What is the customer's tone?", "calm", "frustrated", "angry"),
/// };
/// </code>
/// </example>
public sealed class Questions : Dictionary<string, Question>
{
    /// <summary>Create an empty question set.</summary>
    public Questions() : base(StringComparer.Ordinal) { }
}
