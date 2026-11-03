using System.Collections.ObjectModel;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Study;

/// <summary>Aggregated authoring diagnostics, independent of UI or storage.</summary>
public readonly struct GuidedLessonValidationResult
{
    private readonly IReadOnlyList<string>? _messages;
    internal GuidedLessonValidationResult(IEnumerable<string> messages) =>
        _messages = new ReadOnlyCollection<string>(messages.ToArray());
    /// <summary>Gets all validation errors.</summary>
    public IReadOnlyList<string> Messages => _messages ?? Array.Empty<string>();
    /// <summary>Gets whether authoring errors prevent starting a lesson.</summary>
    public bool HasErrors => Messages.Count != 0;
}

/// <summary>Validates teaching data and its complete scripted move sequence using the shared local chess rules.</summary>
public static class GuidedLessonValidator
{
    /// <summary>
    /// Validates a definition. A serialization adapter may supply earlier setup-conversion errors;
    /// those replace the missing-setup diagnostic and are prefixed as lesson setup errors here.
    /// Objectives are checked when confirming actual moves, not inferred from engine metadata during authoring.
    /// </summary>
    public static GuidedLessonValidationResult Validate(GuidedLessonDefinition lesson, IReadOnlyList<string>? setupErrors = null)
    {
        if (lesson is null) throw new ArgumentNullException(nameof(lesson));
        var messages = new List<string>();
        if (string.IsNullOrWhiteSpace(lesson.Id)) messages.Add("Lesson id is required.");
        if (string.IsNullOrWhiteSpace(lesson.Title)) messages.Add("Lesson title is required.");
        if (!Enum.IsDefined(typeof(GuidedLessonCategory), lesson.Category)) messages.Add("Lesson category is invalid.");
        if (!Enum.IsDefined(typeof(GuidedLessonDifficulty), lesson.Difficulty)) messages.Add("Lesson difficulty is invalid.");
        if (!Enum.IsDefined(typeof(GuidedLessonKind), lesson.Kind)) messages.Add("Lesson kind is invalid.");
        if (!Enum.IsDefined(typeof(GuidedLessonMode), lesson.Mode)) messages.Add("Lesson mode is invalid.");
        var fundamentals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in lesson.Fundamentals)
        {
            if (string.IsNullOrWhiteSpace(id)) messages.Add("Lesson fundamental ids cannot be empty.");
            else if (!fundamentals.Add(id.Trim())) messages.Add($"Duplicate lesson fundamental id '{id.Trim()}'.");
        }
        var position = default(Fen);
        bool hasPosition = false;
        if (setupErrors is { Count: > 0 })
        {
            foreach (string error in setupErrors) messages.Add("Lesson initial setup: " + error.TrimEnd('\r'));
        }
        else if (lesson.InitialSetup is not { } setup) messages.Add("Lesson initial setup is required.");
        else
        {
            try { position = setup.ResolveCurrentFen(); position.ValidateForPlay(); hasPosition = true; }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            { messages.Add("Lesson initial setup: " + exception.Message); }
        }
        if (lesson.Order < 0) messages.Add("Lesson order cannot be negative.");
        if (lesson.Mode == GuidedLessonMode.GoalPuzzle)
        {
            if (lesson.Puzzle is null) messages.Add("Lesson puzzle goal is required.");
            else if (lesson.Puzzle.MaxLearnerMoves < 1) messages.Add("Lesson puzzle move budget must be positive.");
            if (lesson.Steps.Count != 0) messages.Add("Goal puzzles cannot also contain scripted steps.");
            if (hasPosition && position.GetLegalMoves().Length == 0)
                messages.Add("Lesson puzzle must start in a non-terminal position.");
            return new(messages);
        }
        if (lesson.Puzzle is not null) messages.Add("Scripted lessons cannot also define a puzzle goal.");
        if (lesson.Steps.Count == 0) messages.Add("Lesson must contain at least one step.");
        for (int i = 0; i < lesson.Steps.Count; i++)
            hasPosition = ValidateStep(lesson.Steps[i], i + 1, hasPosition, ref position, messages);
        return new(messages);
    }

    private static bool ValidateStep(GuidedLessonStep? step, int number, bool hasPosition, ref Fen position, List<string> messages)
    {
        if (step is null) { messages.Add($"Lesson step {number} is required."); return false; }
        bool hasMove = !string.IsNullOrWhiteSpace(step.ExpectedPlayerMove);
        bool hasReply = !string.IsNullOrWhiteSpace(step.OpponentReply);
        if (string.IsNullOrWhiteSpace(step.Instruction) && string.IsNullOrWhiteSpace(step.BeforeMoveExplanation)
            && !hasMove && string.IsNullOrWhiteSpace(step.AfterMoveExplanation))
            messages.Add($"Lesson step {number} must include instruction, explanation, or expected player move.");
        bool parsedMove = UciCoordinateMove.TryParse(step.ExpectedPlayerMove, out var move);
        bool parsedReply = UciCoordinateMove.TryParse(step.OpponentReply, out var reply);
        if (hasMove && !parsedMove) messages.Add($"Lesson step {number} expected player move must use UCI coordinate notation.");
        if (hasReply && !hasMove) messages.Add($"Lesson step {number} opponent reply requires an expected player move.");
        if (hasReply && !parsedReply) messages.Add($"Lesson step {number} opponent reply must use UCI coordinate notation.");
        if (!Enum.IsDefined(typeof(GuidedLessonObjective), step.Objective)) messages.Add($"Lesson step {number} objective is invalid.");
        if (step.Objective != GuidedLessonObjective.None && !hasMove) messages.Add($"Lesson step {number} objective requires an expected player move.");
        if (!hasPosition || !hasMove) return hasPosition;
        if (!parsedMove || !TryApply(ref position, move.Notation,
            $"Lesson step {number} expected player move is not legal for the lesson position.", messages)) return false;
        return !hasReply || parsedReply && TryApply(ref position, reply.Notation,
            $"Lesson step {number} opponent reply is not legal for the lesson position.", messages);
    }

    private static bool TryApply(ref Fen position, string move, string message, List<string> messages)
    {
        try { position = position.ApplyMove(move); return true; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        { messages.Add(message); return false; }
    }
}

/// <summary>Catalog validation and authored ordering; unlocking belongs to the caller's progression system.</summary>
public static class GuidedLessonCatalog
{
    /// <summary>Validates entries and case-insensitive trimmed IDs, retaining all diagnostics.</summary>
    public static GuidedLessonValidationResult Validate(IEnumerable<GuidedLessonDefinition?>? lessons)
    {
        if (lessons is null) return new(["Lesson catalog data is required."]);
        var entries = lessons.ToArray();
        var messages = new List<string>();
        foreach (var lesson in entries)
            if (lesson is not null) messages.AddRange(GuidedLessonValidator.Validate(lesson).Messages);
        messages.AddRange(ValidateIdentities(entries).Messages);
        return new(messages);
    }

    /// <summary>Checks null entries and unique IDs when adapters already validated their individual serialized entries.</summary>
    public static GuidedLessonValidationResult ValidateIdentities(IEnumerable<GuidedLessonDefinition?>? lessons)
    {
        if (lessons is null) return new(["Lesson catalog data is required."]);
        var messages = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lesson in lessons)
        {
            if (lesson is null) { messages.Add("Lesson catalog cannot contain null entries."); continue; }
            if (!string.IsNullOrWhiteSpace(lesson.Id) && !ids.Add(lesson.Id.Trim()))
                messages.Add($"Duplicate lesson id '{lesson.Id.Trim().ToLowerInvariant()}'.");
        }
        return new(messages);
    }

    /// <summary>Compares authored order, then IDs, with missing entries after valid entries.</summary>
    public static int Compare(int leftOrder, string? leftId, int rightOrder, string? rightId)
    {
        int order = leftOrder.CompareTo(rightOrder);
        return order != 0 ? order : string.Compare(leftId, rightId, StringComparison.OrdinalIgnoreCase);
    }
}
