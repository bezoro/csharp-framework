using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Study;

/// <summary>
/// A reusable, validated scripted exercise. Learner moves may branch; every opponent reply is unambiguous.
/// Completion means following an accepted solution, not an inferred engine objective.
/// </summary>
public sealed class ChessLesson
{
    internal sealed class Node(Fen position, Node? parent, string? move)
    {
        internal readonly Fen Position = position;
        internal readonly Node? Parent = parent;
        internal readonly string? Move = move;
        internal readonly List<Node> Children = new(1);
    }
    internal Node Root { get; }

    /// <summary>
    /// Validates all solution lines once. Lines begin/end with a learner move, cannot extend another accepted
    /// solution, and contain only legal UCI moves. Caller data is copied. Budgets bound authoring/import work.
    /// </summary>
    public ChessLesson(Fen startingPosition, IEnumerable<IEnumerable<string>> acceptedLines,
        int maxNodes = 4096, int maxMovesPerLine = 256)
    {
        if (acceptedLines is null) throw new ArgumentNullException(nameof(acceptedLines));
        if (maxNodes < 2) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (maxMovesPerLine < 1) throw new ArgumentOutOfRangeException(nameof(maxMovesPerLine));
        startingPosition.ValidateForPlay();
        Root = new Node(startingPosition, null, null);
        LearnerSide = startingPosition.ActiveColor;
        int nodes = 1, lines = 0, suppliedLines = 0;
        var endings = new HashSet<Node>();
        foreach (IEnumerable<string> line in acceptedLines)
        {
            if (++suppliedLines > maxNodes) throw new ArgumentException("Lesson solution limit exceeded.", nameof(acceptedLines));
            if (line is null) throw new ArgumentException("Solution lines cannot be null.", nameof(acceptedLines));
            Node current = Root;
            int moves = 0;
            foreach (string move in line)
            {
                if (++moves > maxMovesPerLine) throw new ArgumentException("Lesson line limit exceeded.", nameof(acceptedLines));
                if (endings.Contains(current)) throw new ArgumentException("A solution cannot extend another accepted solution.", nameof(acceptedLines));
                if (move is null) throw new ArgumentException("Moves cannot be null.", nameof(acceptedLines));
                string normalized = move.Trim().ToLowerInvariant();
                Node? next = current.Children.Find(candidate => candidate.Move == normalized);
                if (next is null)
                {
                    if (current.Position.ActiveColor != LearnerSide && current.Children.Count != 0)
                        throw new ArgumentException("Scripted opponent replies must be unambiguous.", nameof(acceptedLines));
                    if (nodes == maxNodes) throw new ArgumentException("Lesson node limit exceeded.", nameof(acceptedLines));
                    Fen nextPosition;
                    try { nextPosition = current.Position.ApplyMove(normalized); }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                    { throw new ArgumentException("A solution contains an illegal move.", nameof(acceptedLines), exception); }
                    next = new Node(nextPosition, current, normalized);
                    current.Children.Add(next);
                    nodes++;
                }
                current = next;
            }
            if (moves == 0 || current.Parent!.Position.ActiveColor != LearnerSide || current.Children.Count != 0)
                throw new ArgumentException("Solutions must end after a learner move and cannot prefix another solution.", nameof(acceptedLines));
            if (endings.Add(current)) lines++;
        }
        if (lines == 0) throw new ArgumentException("At least one accepted solution is required.", nameof(acceptedLines));
    }

    /// <summary>Gets the learner's side; the learner makes the first move in the exercise.</summary>
    public char LearnerSide { get; }
    /// <summary>Gets the starting position.</summary>
    public Fen StartingPosition => Root.Position;
    /// <summary>Creates an independent mutable attempt sharing this immutable solution tree.</summary>
    public ChessLessonAttempt StartAttempt() => new(this);
}

/// <summary>A caller-owned, single-threaded attempt. No engine, clock or per-frame evaluation is needed.</summary>
public sealed class ChessLessonAttempt
{
    private readonly ChessLesson _lesson;
    private ChessLesson.Node _current;
    internal ChessLessonAttempt(ChessLesson lesson) { _lesson = lesson; _current = lesson.Root; }
    /// <summary>Gets the current position, after any automatic scripted reply.</summary>
    public Fen Position => _current.Position;
    /// <summary>Gets whether an accepted solution was completed.</summary>
    public bool IsCompleted => _current.Children.Count == 0;
    /// <summary>Gets rejected move attempts since reset. Undo does not erase mistakes.</summary>
    public int MistakeCount { get; private set; }
    /// <summary>Gets the available learner hints; completed attempts have none.</summary>
    public int HintCount => _current.Children.Count;
    /// <summary>Gets one accepted UCI move without allocating.</summary>
    public string GetHint(int index) => _current.Children[index].Move!;

    /// <summary>
    /// Attempts one learner move. A rejected move leaves the board unchanged; a correct non-final move also
    /// applies the scripted opponent reply. Completed attempts reject further input without adding mistakes.
    /// </summary>
    public LessonMoveResult TryPlay(string move)
    {
        if (move is null) throw new ArgumentNullException(nameof(move));
        if (IsCompleted) return new LessonMoveResult(false, true, null, null);
        string normalized = move.Trim().ToLowerInvariant();
        ChessLesson.Node? next = null;
        foreach (var candidate in _current.Children)
            if (candidate.Move == normalized) { next = candidate; break; }
        if (next is null)
        {
            if (MistakeCount != int.MaxValue) MistakeCount++;
            return new LessonMoveResult(false, false, null, null);
        }
        _current = next;
        string? reply = null;
        if (_current.Children.Count != 0)
        {
            _current = _current.Children[0];
            reply = _current.Move;
        }
        return new LessonMoveResult(true, IsCompleted, normalized, reply);
    }
    /// <summary>Rewinds the last learner decision and its scripted reply, retaining the mistake count.</summary>
    public bool Undo()
    {
        if (_current.Parent is null) return false;
        do { _current = _current.Parent!; }
        while (_current.Parent is not null && _current.Position.ActiveColor != _lesson.LearnerSide);
        return true;
    }
    /// <summary>Returns to the starting position and clears mistakes.</summary>
    public void Reset() { _current = _lesson.Root; MistakeCount = 0; }
}

/// <summary>Immediate exercise feedback, including the optional automatic opponent reply.</summary>
public readonly struct LessonMoveResult
{
    internal LessonMoveResult(bool accepted, bool completed, string? learnerMove, string? reply)
    { Accepted = accepted; Completed = completed; LearnerMove = learnerMove; OpponentReply = reply; }
    /// <summary>Gets whether the learner move was accepted.</summary>
    public bool Accepted { get; }
    /// <summary>Gets whether the exercise is complete after the request.</summary>
    public bool Completed { get; }
    /// <summary>Gets the accepted learner move, or null on rejection.</summary>
    public string? LearnerMove { get; }
    /// <summary>Gets the scripted reply applied after the learner move, if any.</summary>
    public string? OpponentReply { get; }
}
