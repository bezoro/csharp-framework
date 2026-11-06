using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Study;

/// <summary>The subject of an authored teaching phase.</summary>
public enum GuidedLessonCategory
{
    /// <summary>Piece movement and other basic rules.</summary>
    Rules = 0,
    /// <summary>Opening play.</summary>
    Opening = 1,
    /// <summary>Positional decisions.</summary>
    Strategy = 2,
    /// <summary>Forcing tactical ideas.</summary>
    Tactics = 3,
    /// <summary>An authored challenge.</summary>
    Challenge = 4
}

/// <summary>The authored difficulty, independent of progression or unlocking.</summary>
public enum GuidedLessonDifficulty
{
    /// <summary>Introductory material.</summary>
    Beginner = 0,
    /// <summary>Standard material.</summary>
    Normal = 1,
    /// <summary>Advanced material.</summary>
    Advanced = 2
}

/// <summary>Whether a phase teaches a fundamental or reinforces earlier fundamentals through a boss challenge.</summary>
public enum GuidedLessonKind
{
    /// <summary>A phase that introduces or practises a fundamental.</summary>
    Fundamental = 0,
    /// <summary>A phase requiring an authored sequence that reinforces earlier fundamentals.</summary>
    Boss = 1
}

/// <summary>Whether teaching requires authored moves or allows the learner to solve a goal.</summary>
public enum GuidedLessonMode
{
    /// <summary>Accept only the authored learner move, with an optional scripted reply.</summary>
    Scripted = 0,
    /// <summary>Accept legal learner moves and verify the goal against confirmed local positions.</summary>
    GoalPuzzle = 1
}

/// <summary>An authored checkmate goal with a budget measured in learner moves, excluding setup history.</summary>
public sealed class ChessPuzzleDefinition
{
    /// <summary>Creates a checkmate-within-N-learner-moves goal.</summary>
    public ChessPuzzleDefinition(int maxLearnerMoves, string? instruction = null)
    {
        MaxLearnerMoves = maxLearnerMoves;
        Instruction = instruction ?? string.Empty;
    }
    /// <summary>Gets the maximum number of moves made by the learner after setup.</summary>
    public int MaxLearnerMoves { get; }
    /// <summary>Gets the authored instruction shown throughout the puzzle.</summary>
    public string Instruction { get; }
}

/// <summary>A locally verified outcome of the learner's authored move.</summary>
public enum GuidedLessonObjective
{
    /// <summary>Require only the authored move.</summary>
    None = 0,
    /// <summary>Apply the authored piece move.</summary>
    MovePiece = 1,
    /// <summary>Capture an opposing piece.</summary>
    Capture = 2,
    /// <summary>Give check to the opponent.</summary>
    GiveCheck = 3,
    /// <summary>Make a legal move from a checked position, including countercheck.</summary>
    EscapeCheck = 4,
    /// <summary>Checkmate the opposing king.</summary>
    Checkmate = 5,
    /// <summary>Promote a pawn.</summary>
    Promote = 6,
    /// <summary>Castle.</summary>
    Castle = 7
}

/// <summary>One immutable explanation or learner decision, with an optional scripted opponent reply.</summary>
public sealed class GuidedLessonStep
{
    /// <summary>Creates an authored step. Empty moves describe explanation-only steps.</summary>
    public GuidedLessonStep(string? instruction = null, string? beforeMoveExplanation = null,
        string? expectedPlayerMove = null, string? opponentReply = null, string? afterMoveExplanation = null,
        GuidedLessonObjective objective = GuidedLessonObjective.None)
    {
        Instruction = instruction ?? string.Empty;
        BeforeMoveExplanation = beforeMoveExplanation ?? string.Empty;
        ExpectedPlayerMove = expectedPlayerMove ?? string.Empty;
        OpponentReply = opponentReply ?? string.Empty;
        AfterMoveExplanation = afterMoveExplanation ?? string.Empty;
        Objective = objective;
    }

    /// <summary>Gets the learner instruction.</summary>
    public string Instruction { get; }
    /// <summary>Gets the explanation shown before the learner's move.</summary>
    public string BeforeMoveExplanation { get; }
    /// <summary>Gets the required UCI move, or an empty string for an explanation-only step.</summary>
    public string ExpectedPlayerMove { get; }
    /// <summary>Gets the optional scripted UCI reply.</summary>
    public string OpponentReply { get; }
    /// <summary>Gets feedback shown after the accepted decision.</summary>
    public string AfterMoveExplanation { get; }
    /// <summary>Gets the rule outcome required after applying the learner move.</summary>
    public GuidedLessonObjective Objective { get; }
}

/// <summary>
/// Immutable authoring data for a discrete teaching phase. Validation reports all authoring errors;
/// no engine, storage, unlocking, UI or asynchronous operations are owned by this definition.
/// </summary>
public sealed class GuidedLessonDefinition
{
    /// <summary>Copies the steps and fundamental IDs. An absent setup is reported by validation.</summary>
    public GuidedLessonDefinition(string? id, string? title, PlayableMatchSetup? initialSetup,
        IEnumerable<GuidedLessonStep>? steps, string? description = null,
        GuidedLessonCategory category = GuidedLessonCategory.Rules,
        GuidedLessonDifficulty difficulty = GuidedLessonDifficulty.Beginner, int order = 0,
        GuidedLessonKind kind = GuidedLessonKind.Fundamental, IEnumerable<string>? fundamentals = null,
        GuidedLessonMode mode = GuidedLessonMode.Scripted, ChessPuzzleDefinition? puzzle = null)
    {
        Id = id ?? string.Empty;
        Title = title ?? string.Empty;
        Description = description ?? string.Empty;
        Category = category;
        Difficulty = difficulty;
        Order = order;
        Kind = kind;
        Mode = mode;
        Puzzle = puzzle;
        InitialSetup = initialSetup;
        Steps = new ReadOnlyCollection<GuidedLessonStep>((steps ?? []).ToArray());
        Fundamentals = new ReadOnlyCollection<string>((fundamentals ?? []).ToArray());
        Fingerprint = ComputeFingerprint();
    }

    /// <summary>Gets the phase ID used by catalogs and external progression systems.</summary>
    public string Id { get; }
    /// <summary>Gets its displayed title.</summary>
    public string Title { get; }
    /// <summary>Gets its description.</summary>
    public string Description { get; }
    /// <summary>Gets its subject.</summary>
    public GuidedLessonCategory Category { get; }
    /// <summary>Gets its difficulty.</summary>
    public GuidedLessonDifficulty Difficulty { get; }
    /// <summary>Gets its authored ordering key.</summary>
    public int Order { get; }
    /// <summary>Gets whether the phase teaches or reinforces fundamentals.</summary>
    public GuidedLessonKind Kind { get; }
    /// <summary>Gets whether moves are scripted or selected freely to solve a goal.</summary>
    public GuidedLessonMode Mode { get; }
    /// <summary>Gets the goal definition when the mode is <see cref="GuidedLessonMode.GoalPuzzle"/>.</summary>
    public ChessPuzzleDefinition? Puzzle { get; }
    /// <summary>Gets the IDs of the fundamentals taught or reinforced; these do not imply unlock rules.</summary>
    public IReadOnlyList<string> Fundamentals { get; }
    /// <summary>Gets the starting position and any pre-existing move history.</summary>
    public PlayableMatchSetup? InitialSetup { get; }
    /// <summary>Gets the copied ordered teaching steps.</summary>
    public IReadOnlyList<GuidedLessonStep> Steps { get; }

    /// <summary>Gets the SHA256 identity of the teaching content and authored position/history; runtime clock snapshots are excluded.</summary>
    public string Fingerprint { get; }

    /// <summary>Replays preceding scripted steps for an authoring preview. Invalid data throws without changing the definition.</summary>
    public Fen GetPositionBeforeStep(int index)
    {
        if (Mode != GuidedLessonMode.Scripted) throw new InvalidOperationException("Goal puzzles do not have scripted step positions.");
        if ((uint)index >= (uint)Steps.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var position = InitialSetup?.ResolveCurrentFen() ?? throw new InvalidOperationException("The lesson has no initial setup.");
        for (int i = 0; i < index; i++)
        {
            var step = Steps[i];
            if (!string.IsNullOrWhiteSpace(step.ExpectedPlayerMove)) position = position.ApplyMove(step.ExpectedPlayerMove);
            if (!string.IsNullOrWhiteSpace(step.OpponentReply)) position = position.ApplyMove(step.OpponentReply);
        }
        return position;
    }

    private string ComputeFingerprint()
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("Bezoro.GuidedLesson.v1");
            writer.Write(Id); writer.Write(Title); writer.Write(Description);
            writer.Write((int)Category); writer.Write((int)Difficulty); writer.Write(Order); writer.Write((int)Kind);
            writer.Write((int)Mode); writer.Write(Puzzle is not null);
            if (Puzzle is not null) { writer.Write(Puzzle.MaxLearnerMoves); writer.Write(Puzzle.Instruction); }
            writer.Write(Fundamentals.Count);
            foreach (string fundamental in Fundamentals) writer.Write(fundamental ?? string.Empty);
            writer.Write(InitialSetup.HasValue);
            if (InitialSetup is { } setup)
            {
                var fen = setup.BaseFen;
                writer.Write(fen.PiecePlacement ?? string.Empty); writer.Write(fen.ActiveColor);
                writer.Write(fen.CastlingRights ?? string.Empty); writer.Write(fen.EnPassantTarget ?? string.Empty);
                writer.Write(fen.HalfmoveClock); writer.Write(fen.FullmoveNumber);
                writer.Write(setup.PlayedMoves.IsDefault ? 0 : setup.PlayedMoves.Length);
                if (!setup.PlayedMoves.IsDefault)
                    foreach (string move in setup.PlayedMoves) writer.Write(move ?? string.Empty);
            }
            writer.Write(Steps.Count);
            foreach (var step in Steps)
            {
                writer.Write(step is not null);
                if (step is null) continue;
                writer.Write(step.Instruction); writer.Write(step.BeforeMoveExplanation); writer.Write(step.ExpectedPlayerMove);
                writer.Write(step.OpponentReply); writer.Write(step.AfterMoveExplanation); writer.Write((int)step.Objective);
            }
        }
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", string.Empty).ToLowerInvariant();
    }
}

/// <summary>A completed, locally verified attempt. Only the guided player can issue this receipt.</summary>
public sealed class ChessLessonCompletion
{
    internal ChessLessonCompletion(GuidedLessonDefinition definition, Guid attemptId, int mistakeCount)
    { LessonId = definition.Id; DefinitionFingerprint = definition.Fingerprint; AttemptId = attemptId; MistakeCount = mistakeCount; }
    /// <summary>Gets the completed authored phase ID.</summary>
    public string LessonId { get; }
    /// <summary>Gets the exact teaching-definition fingerprint accepted by the player.</summary>
    public string DefinitionFingerprint { get; }
    /// <summary>Gets the unique attempt receipt, retained until reset or a new lesson starts.</summary>
    public Guid AttemptId { get; }
    /// <summary>Gets the number of rejected moves made while this attempt awaited learner input.</summary>
    public int MistakeCount { get; }
}

/// <summary>An immutable position and history reported by an external match adapter.</summary>
public readonly struct GuidedLessonPosition
{
    /// <summary>Creates a confirmation snapshot. The player verifies the position against the reported history.</summary>
    public GuidedLessonPosition(PlayableMatchSetup history, Fen position)
    { History = history; Position = position; }
    /// <summary>Gets the reported base position and played moves.</summary>
    public PlayableMatchSetup History { get; }
    /// <summary>Gets the reported current position.</summary>
    public Fen Position { get; }
}

/// <summary>Move matching shared by guided authoring and players.</summary>
public static class GuidedLessonMoves
{
    /// <summary>Matches normalized UCI moves, optionally accepting a pending promotion choice with the same travel.</summary>
    public static bool IsExpected(string? expected, string? candidate, bool allowPendingPromotion = true)
    {
        if (!UciCoordinateMove.TryParse(expected, out var wanted) || !UciCoordinateMove.TryParse(candidate, out var actual))
            return false;
        return wanted == actual || allowPendingPromotion && wanted.IsPromotion && !actual.IsPromotion
            && wanted.From == actual.From && wanted.To == actual.To;
    }
}
