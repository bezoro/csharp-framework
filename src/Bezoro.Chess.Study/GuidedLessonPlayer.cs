using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.StateMachines;

namespace Bezoro.Chess.Study;

/// <summary>The discrete phases of a teaching attempt. Values are stable for serialized UI adapters.</summary>
public enum GuidedLessonPhase
{
    /// <summary>No lesson owns learner input.</summary>
    Inactive = 0,
    /// <summary>The host is loading the authored setup.</summary>
    LoadingSetup = 1,
    /// <summary>An explanation awaits explicit continuation.</summary>
    ShowingExplanation = 2,
    /// <summary>The learner may request an allowed move.</summary>
    ReadyForPlayerMove = 3,
    /// <summary>An accepted request awaits an actual position/history confirmation.</summary>
    WaitingForMoveResult = 4,
    /// <summary>The host must apply a scripted or engine-controlled opponent move.</summary>
    WaitingForOpponentReply = 5,
    /// <summary>The authored lesson was completed.</summary>
    Completed = 6,
    /// <summary>The objective or external operation failed.</summary>
    Failed = 7
}

/// <summary>Whether the match host may submit a learner move.</summary>
public readonly record struct GuidedLessonMoveRequest(bool CanSubmitMove, string Feedback);

/// <summary>Progression after a confirmed move or explanation.</summary>
public readonly record struct GuidedLessonStepResult(bool DidAdvance, bool IsLessonComplete, string Feedback,
    string OpponentReply, bool NeedsOpponentMove = false)
{
    /// <summary>Gets whether the host must apply the exact scripted reply.</summary>
    public bool HasOpponentReply => !string.IsNullOrWhiteSpace(OpponentReply);
}

/// <summary>Read-only teaching output and authoritative input constraints for presentation adapters.</summary>
public readonly struct GuidedLessonProjection
{
    private readonly ImmutableArray<string> _allowedMoves;
    private readonly IReadOnlyList<char>? _allowedPromotionPieces;
    private readonly int _undoPlies;
    internal GuidedLessonProjection(GuidedLessonPhase phase, string title, string instruction, string feedback,
        int index, int count, UciCoordinateMove? requiredMove, ImmutableArray<string> allowedMoves,
        int learnerMoves, int maxLearnerMoves, IReadOnlyList<char> allowedPromotionPieces, int undoPlies)
    {
        Phase = phase; Title = title; Instruction = instruction; Feedback = feedback;
        CurrentStepIndex = index; StepCount = count; RequiredMove = requiredMove; _allowedMoves = allowedMoves;
        LearnerMoves = learnerMoves; MaxLearnerMoves = maxLearnerMoves;
        _allowedPromotionPieces = allowedPromotionPieces;
        _undoPlies = undoPlies;
    }
    /// <summary>Gets the current attempt phase.</summary>
    public GuidedLessonPhase Phase { get; }
    /// <summary>Gets the displayed lesson title.</summary>
    public string Title { get; }
    /// <summary>Gets the current teaching instruction.</summary>
    public string Instruction { get; }
    /// <summary>Gets current feedback.</summary>
    public string Feedback { get; }
    /// <summary>Gets the zero-based teaching step, or -1 without a lesson.</summary>
    public int CurrentStepIndex { get; }
    /// <summary>Gets the scripted step count, or one for a goal puzzle.</summary>
    public int StepCount { get; }
    /// <summary>Gets whether a lesson owns learner input.</summary>
    public bool IsActive => Phase != GuidedLessonPhase.Inactive;
    /// <summary>Gets whether input is free play or currently allowed by the lesson.</summary>
    public bool AcceptsMoveRequests => Phase is GuidedLessonPhase.Inactive or GuidedLessonPhase.ReadyForPlayerMove;
    /// <summary>Gets the exact authored move for a scripted learner step, otherwise null. UI arrows use these parsed squares.</summary>
    public UciCoordinateMove? RequiredMove { get; }
    /// <summary>Gets the number of confirmed learner moves since the authored puzzle setup.</summary>
    public int LearnerMoves { get; }
    /// <summary>Gets the puzzle budget, or zero for scripted lessons.</summary>
    public int MaxLearnerMoves { get; }
    /// <summary>Gets the authoritative pending promotion choices, as normalized q/r/b/n, otherwise an empty list.</summary>
    public IReadOnlyList<char> AllowedPromotionPieces => _allowedPromotionPieces ?? Array.Empty<char>();
    /// <summary>Gets whether undo is free play or can rewind one completed puzzle decision. Scripted phases use restart.</summary>
    public bool AcceptsUndoRequests => Phase == GuidedLessonPhase.Inactive || _undoPlies > 0;
    /// <summary>Preserves free-play requests; puzzles rewind the learner decision and its reply without crossing the setup.</summary>
    public int GetUndoPlyCount(int requestedCount) => requestedCount < 1 ? 0 : Phase == GuidedLessonPhase.Inactive ? requestedCount : _undoPlies;
    /// <summary>Intersects host legal moves with the player's local-rule constraints. Inactive lessons preserve free play.</summary>
    public IReadOnlyList<string> GetAllowedMoves(IEnumerable<string> hostLegalMoves)
    {
        if (hostLegalMoves is null) throw new ArgumentNullException(nameof(hostLegalMoves));
        if (!AcceptsMoveRequests) return Array.Empty<string>();
        var result = new List<string>();
        foreach (string candidate in hostLegalMoves)
        {
            if (!UciCoordinateMove.TryParse(candidate, out var move)) continue;
            if (Phase == GuidedLessonPhase.Inactive || !_allowedMoves.IsDefault && _allowedMoves.Contains(move.Notation))
                result.Add(move.Notation);
        }
        return result;
    }
}

/// <summary>
/// Caller-owned, single-threaded teaching state. The host performs I/O, then supplies immutable position/history
/// snapshots; only shared local rules can confirm moves, objectives, puzzle budgets and completion receipts.
/// </summary>
public sealed class GuidedLessonPlayer
{
    private enum Signal { Load, Explain, AwaitLearner, Submit, AwaitReply, Complete, Fail, Reset }
    private static readonly StateMachineDefinition Flow = CreateFlow();
    private StateId _state = Flow.InitialState;
    private GuidedLessonDefinition? _lesson;
    private GuidedLessonPosition? _confirmed;
    private string _feedback = string.Empty;
    private string _pendingMove = string.Empty;
    private int _pendingHistoryCount;
    private char _learnerSide;
    private int _learnerMoves;
    private Guid _attemptId;

    /// <summary>Gets the current teaching phase.</summary>
    public GuidedLessonPhase Phase => (GuidedLessonPhase)_state.Value;
    /// <summary>Gets the current zero-based step, or -1 without a lesson.</summary>
    public int CurrentStepIndex { get; private set; } = -1;
    /// <summary>Gets rejected requests made while awaiting learner input. Blocked phases do not count as mistakes.</summary>
    public int MistakeCount { get; private set; }
    /// <summary>Gets the receipt of this completed attempt, cleared by reset or a new attempt.</summary>
    public ChessLessonCompletion? Completion { get; private set; }

    /// <summary>Checks a pending promotion choice against the same authoritative constraint used by presentation.</summary>
    public bool AcceptsPromotion(char piece) => Phase == GuidedLessonPhase.Inactive || Project().AllowedPromotionPieces.Contains(char.ToLowerInvariant(piece));

    /// <summary>Validates and owns a copied definition while the host loads its setup.</summary>
    public bool BeginLoading(GuidedLessonDefinition lesson, out string error, IReadOnlyList<string>? setupErrors = null)
    {
        var validation = GuidedLessonValidator.Validate(lesson, setupErrors);
        if (validation.HasErrors) { error = string.Join("\n", validation.Messages); Reset(); return false; }
        _lesson = lesson;
        _confirmed = new GuidedLessonPosition(lesson.InitialSetup!.Value, lesson.InitialSetup.Value.ResolveCurrentFen());
        _learnerSide = _confirmed.Value.Position.ActiveColor;
        _feedback = _pendingMove = string.Empty;
        _learnerMoves = MistakeCount = 0;
        _attemptId = Guid.NewGuid();
        Completion = null;
        CurrentStepIndex = 0;
        Transition(Signal.Load);
        error = string.Empty;
        return true;
    }

    /// <summary>Starts a locally owned attempt when no external setup load is required.</summary>
    public bool Start(GuidedLessonDefinition lesson, out string error)
    {
        if (!BeginLoading(lesson, out error)) return false;
        CompleteSetupLoading();
        return true;
    }

    /// <summary>Acknowledges setup I/O; stale acknowledgements outside LoadingSetup do nothing.</summary>
    public void CompleteSetupLoading()
    {
        if (_lesson is null || Phase != GuidedLessonPhase.LoadingSetup) return;
        Transition(_lesson.Mode == GuidedLessonMode.GoalPuzzle || RequiresMove(CurrentStep) ? Signal.AwaitLearner : Signal.Explain);
    }

    /// <summary>Releases lesson ownership and any completion receipt.</summary>
    public void Reset()
    {
        _lesson = null; _confirmed = null; _feedback = _pendingMove = string.Empty;
        CurrentStepIndex = -1; _learnerMoves = MistakeCount = 0; Completion = null;
        _attemptId = Guid.Empty;
        Transition(Signal.Reset);
    }

    /// <summary>Records an external-operation failure without claiming objective completion.</summary>
    public void Fail(string? feedback)
    { _feedback = feedback ?? string.Empty; Completion = null; Transition(Signal.Fail); }

    /// <summary>Accepts only the current exact scripted move or a legal puzzle move; no board state changes here.</summary>
    public GuidedLessonMoveRequest EvaluateMoveRequest(string move, int playedMoveCount = 0)
    {
        if (Phase == GuidedLessonPhase.Inactive) return new(true, string.Empty);
        if (_lesson is null || Phase != GuidedLessonPhase.ReadyForPlayerMove) return new(false, _feedback);
        bool accepted = _lesson.Mode == GuidedLessonMode.Scripted
            ? GuidedLessonMoves.IsExpected(CurrentStep.ExpectedPlayerMove, move)
            : _confirmed!.Value.Position.GetLegalMoves().Any(legal => GuidedLessonMoves.IsExpected(legal, move));
        if (!accepted)
        {
            if (MistakeCount != int.MaxValue) MistakeCount++;
            _feedback = _lesson.Mode == GuidedLessonMode.Scripted
                ? "That is legal chess, but not the lesson move. Try again." : "That move is not legal in this position.";
            return new(false, _feedback);
        }
        UciCoordinateMove.TryNormalize(move, out _pendingMove);
        _pendingHistoryCount = Math.Max(playedMoveCount, _confirmed!.Value.History.PlayedMoves.Length);
        _feedback = string.Empty;
        Transition(Signal.Submit);
        return new(true, _feedback);
    }

    /// <summary>Ignores clock/search/noop or unrelated updates until history confirms the accepted learner move.</summary>
    public GuidedLessonStepResult ObserveAcceptedMove(GuidedLessonPosition state)
    {
        if (Phase != GuidedLessonPhase.WaitingForMoveResult || state.History.PlayedMoves.IsDefault
            || state.History.PlayedMoves.Length <= _pendingHistoryCount) return Pending();
        string last = state.History.PlayedMoves[^1];
        if (!GuidedLessonMoves.IsExpected(_pendingMove, last) && !GuidedLessonMoves.IsExpected(last, _pendingMove)) return Pending();
        if (!TryConfirmMove(state, _pendingMove, out _, out _)) return Pending();
        return CompleteAcceptedMove(state);
    }

    /// <summary>Confirms an explicitly completed learner operation. Invalid or unapplied outcomes fail without advancing.</summary>
    public GuidedLessonStepResult CompleteAcceptedMove(GuidedLessonPosition state)
    {
        if (_lesson is null || Phase != GuidedLessonPhase.WaitingForMoveResult) return Pending();
        string expected = _lesson.Mode == GuidedLessonMode.Scripted ? CurrentStep.ExpectedPlayerMove : _pendingMove;
        if (!TryConfirmMove(state, expected, out var before, out string actual)
            || !ObjectiveSatisfied(_lesson.Mode == GuidedLessonMode.Scripted ? CurrentStep.Objective : GuidedLessonObjective.None, before, actual))
        {
            Fail(_lesson.Mode == GuidedLessonMode.Scripted && CurrentStep.Objective == GuidedLessonObjective.Checkmate
                ? "The move matched the lesson, but the resulting position was not checkmate."
                : "The move matched the lesson, but the lesson objective was not satisfied.");
            return Pending();
        }
        _confirmed = state;
        _pendingMove = string.Empty;
        if (_lesson.Mode == GuidedLessonMode.GoalPuzzle) { _learnerMoves++; return EvaluatePuzzlePosition(); }
        if (!string.IsNullOrWhiteSpace(CurrentStep.OpponentReply))
        {
            _feedback = CurrentStep.AfterMoveExplanation;
            Transition(Signal.AwaitReply);
            UciCoordinateMove.TryNormalize(CurrentStep.OpponentReply, out string reply);
            return new(false, false, _feedback, reply);
        }
        return Advance(CurrentStep.AfterMoveExplanation);
    }

    /// <summary>Confirms the actual scripted or engine-controlled reply before advancing.</summary>
    public GuidedLessonStepResult CompleteOpponentReply(GuidedLessonPosition state)
    {
        if (_lesson is null || Phase != GuidedLessonPhase.WaitingForOpponentReply) return Pending();
        string expected = _lesson.Mode == GuidedLessonMode.Scripted ? CurrentStep.OpponentReply : string.Empty;
        if (!TryConfirmMove(state, expected, out _, out _))
        { Fail("The session did not confirm the lesson reply was applied."); return Pending(); }
        _confirmed = state;
        if (_lesson.Mode == GuidedLessonMode.GoalPuzzle) return EvaluatePuzzlePosition();
        return Advance(_feedback);
    }

    /// <summary>Advances an explanation-only scripted step; other phases remain unchanged.</summary>
    public GuidedLessonStepResult ContinueExplanationStep()
    {
        if (_lesson is null || Phase != GuidedLessonPhase.ShowingExplanation || RequiresMove(CurrentStep)) return Pending();
        return Advance(CurrentStep.AfterMoveExplanation);
    }

    /// <summary>Rewinds a goal puzzle to an actual earlier confirmed-history prefix, recomputing the learner budget.</summary>
    public bool ObserveUndo(GuidedLessonPosition state)
    {
        if (_lesson?.Mode != GuidedLessonMode.GoalPuzzle || _confirmed is not { } confirmed
            || !HasSetupPrefix(state) || state.History.PlayedMoves.Length >= confirmed.History.PlayedMoves.Length
            || !IsPrefix(state.History.PlayedMoves, confirmed.History.PlayedMoves) || !IsConsistent(state)) return false;
        _confirmed = state;
        var setup = _lesson.InitialSetup!.Value;
        var position = setup.ResolveCurrentFen();
        _learnerMoves = 0;
        for (int i = setup.PlayedMoves.Length; i < state.History.PlayedMoves.Length; i++)
        {
            if (position.ActiveColor == _learnerSide) _learnerMoves++;
            position = position.ApplyMove(state.History.PlayedMoves[i]);
        }
        Completion = null; _pendingMove = _feedback = string.Empty;
        // A legal history prefix determines whose turn resumes; no engine or UI guesses this phase.
        Transition(position.ActiveColor == _learnerSide ? Signal.AwaitLearner : Signal.AwaitReply);
        return true;
    }

    /// <summary>Projects the current phase, teaching text, exact hint and locally verified allowed moves.</summary>
    public GuidedLessonProjection Project()
    {
        bool scripted = _lesson?.Mode == GuidedLessonMode.Scripted;
        UciCoordinateMove? required = Phase == GuidedLessonPhase.ReadyForPlayerMove && scripted
            && UciCoordinateMove.TryParse(CurrentStep.ExpectedPlayerMove, out var move) ? move : null;
        var allowed = ImmutableArray<string>.Empty;
        if (Phase == GuidedLessonPhase.ReadyForPlayerMove && _confirmed is { } confirmed)
            allowed = confirmed.Position.GetLegalMoves().Where(candidate => !scripted || GuidedLessonMoves.IsExpected(CurrentStep.ExpectedPlayerMove, candidate, false)).ToImmutableArray();
        string instruction = _lesson is null ? string.Empty : scripted ? BuildInstruction(CurrentStep) : _lesson.Puzzle!.Instruction;
        IReadOnlyList<char> promotions = Array.Empty<char>();
        if (Phase == GuidedLessonPhase.WaitingForMoveResult && _pendingMove.Length == 4 && _confirmed is { } pending)
            promotions = Array.AsReadOnly(pending.Position.GetLegalMoves()
                .Where(candidate => candidate.Length == 5 && candidate.StartsWith(_pendingMove, StringComparison.OrdinalIgnoreCase)
                    && (!scripted || GuidedLessonMoves.IsExpected(CurrentStep.ExpectedPlayerMove, candidate, false)))
                .Select(candidate => candidate[4]).Distinct().ToArray());
        int undoPlies = 0;
        if (_lesson?.Mode == GuidedLessonMode.GoalPuzzle && _confirmed is { } current
            && Phase is GuidedLessonPhase.ReadyForPlayerMove or GuidedLessonPhase.Completed or GuidedLessonPhase.Failed)
        {
            int available = current.History.PlayedMoves.Length - _lesson.InitialSetup!.Value.PlayedMoves.Length;
            undoPlies = Math.Min(available, current.Position.ActiveColor == _learnerSide ? 2 : 1);
        }
        return new(Phase, _lesson?.Title ?? string.Empty, instruction, _feedback, CurrentStepIndex,
            _lesson is null ? 0 : scripted ? _lesson.Steps.Count : 1, required, allowed, _learnerMoves, _lesson?.Puzzle?.MaxLearnerMoves ?? 0, promotions, undoPlies);
    }

    private GuidedLessonStep CurrentStep => _lesson!.Steps[CurrentStepIndex];
    private GuidedLessonStepResult Pending() => new(false, Phase == GuidedLessonPhase.Completed, _feedback, string.Empty);
    private static bool RequiresMove(GuidedLessonStep step) => !string.IsNullOrWhiteSpace(step.ExpectedPlayerMove);
    private static string BuildInstruction(GuidedLessonStep step) => string.IsNullOrWhiteSpace(step.Instruction)
        ? step.BeforeMoveExplanation : string.IsNullOrWhiteSpace(step.BeforeMoveExplanation) ? step.Instruction : step.Instruction + "\n\n" + step.BeforeMoveExplanation;

    private GuidedLessonStepResult Advance(string feedback)
    {
        if (!string.IsNullOrWhiteSpace(feedback)) _feedback = feedback;
        if (CurrentStepIndex + 1 >= _lesson!.Steps.Count) return Complete();
        CurrentStepIndex++;
        Transition(RequiresMove(CurrentStep) ? Signal.AwaitLearner : Signal.Explain);
        return new(true, false, _feedback, string.Empty);
    }

    private GuidedLessonStepResult Complete()
    {
        if (string.IsNullOrWhiteSpace(_feedback)) _feedback = "Lesson complete.";
        Transition(Signal.Complete);
        Completion = new ChessLessonCompletion(_lesson!, _attemptId, MistakeCount);
        return new(true, true, _feedback, string.Empty);
    }

    private GuidedLessonStepResult EvaluatePuzzlePosition()
    {
        var position = _confirmed!.Value.Position;
        if (position.GetLegalMoves().Length == 0)
        {
            if (position.ActiveColor != _learnerSide && position.IsInCheck()) return Complete();
            Fail(position.IsInCheck() ? "The learner was checkmated." : "The puzzle ended in stalemate.");
            return Pending();
        }
        if (_learnerMoves >= _lesson!.Puzzle!.MaxLearnerMoves)
        { Fail("The checkmate move budget was exhausted."); return Pending(); }
        bool learnerTurn = position.ActiveColor == _learnerSide;
        Transition(learnerTurn ? Signal.AwaitLearner : Signal.AwaitReply);
        return new(false, false, _feedback, string.Empty, !learnerTurn);
    }

    private bool TryConfirmMove(GuidedLessonPosition state, string expected, out Fen before, out string actual)
    {
        before = default; actual = string.Empty;
        if (_confirmed is not { } confirmed || state.History.PlayedMoves.IsDefaultOrEmpty || !IsConsistent(state)) return false;
        var moves = state.History.PlayedMoves;
        if (moves.Length != confirmed.History.PlayedMoves.Length + 1 || !SamePosition(state.History.BaseFen, confirmed.History.BaseFen)
            || !IsPrefix(confirmed.History.PlayedMoves, moves)
            || Phase == GuidedLessonPhase.WaitingForMoveResult && moves.Length != _pendingHistoryCount + 1) return false;
        actual = moves[^1];
        if (expected.Length != 0 && !GuidedLessonMoves.IsExpected(expected, actual)
            && !GuidedLessonMoves.IsExpected(actual, expected)) return false;
        try
        {
            before = new PlayableMatchSetup(state.History.BaseFen, moves.RemoveAt(moves.Length - 1)).ResolveCurrentFen();
            if (!SamePosition(before, confirmed.Position)) return false;
            return _lesson!.Mode != GuidedLessonMode.GoalPuzzle || HasSetupPrefix(state);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException) { return false; }
    }

    private bool HasSetupPrefix(GuidedLessonPosition state) => !state.History.PlayedMoves.IsDefault
        && SamePosition(state.History.BaseFen, _lesson!.InitialSetup!.Value.BaseFen)
        && IsPrefix(_lesson.InitialSetup.Value.PlayedMoves, state.History.PlayedMoves);
    private static bool IsPrefix(ImmutableArray<string> prefix, ImmutableArray<string> sequence) => prefix.Length <= sequence.Length
        && prefix.Select((move, index) => string.Equals(move, sequence[index], StringComparison.Ordinal)).All(equal => equal);
    private static bool IsConsistent(GuidedLessonPosition state)
    {
        try { return SamePosition(state.History.ResolveCurrentFen(), state.Position); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException) { return false; }
    }
    private static bool SamePosition(Fen left, Fen right) => left.PiecePlacement == right.PiecePlacement && left.ActiveColor == right.ActiveColor
        && left.CastlingRights == right.CastlingRights && left.EnPassantTarget == right.EnPassantTarget
        && left.HalfmoveClock == right.HalfmoveClock && left.FullmoveNumber == right.FullmoveNumber;
    private static bool ObjectiveSatisfied(GuidedLessonObjective objective, Fen before, string move)
    {
        if (objective is GuidedLessonObjective.None or GuidedLessonObjective.MovePiece) return true;
        if (objective == GuidedLessonObjective.EscapeCheck) return before.IsInCheck();
        var classification = before.ClassifyMoveFully(move);
        return objective switch
        {
            GuidedLessonObjective.Capture => classification.IsCapture,
            GuidedLessonObjective.GiveCheck => classification.IsCheck,
            GuidedLessonObjective.Checkmate => classification.IsMate,
            GuidedLessonObjective.Promote => classification.IsPromotion,
            GuidedLessonObjective.Castle => classification.IsCastling,
            _ => false
        };
    }

    private void Transition(Signal signal)
    {
        var step = Flow.Step(_state, new SignalId((int)signal));
        if (!step.HasTransition) throw new InvalidOperationException($"Cannot apply {signal} while the lesson is {Phase}.");
        _state = step.CurrentState;
    }
    private static StateMachineDefinition CreateFlow()
    {
        var transitions = new List<StateTransition>();
        void Add(GuidedLessonPhase from, Signal signal, GuidedLessonPhase to) =>
            transitions.Add(new(new StateId((int)from), new SignalId((int)signal), new StateId((int)to)));
        foreach (GuidedLessonPhase phase in Enum.GetValues(typeof(GuidedLessonPhase)))
        {
            Add(phase, Signal.Load, GuidedLessonPhase.LoadingSetup); Add(phase, Signal.Reset, GuidedLessonPhase.Inactive); Add(phase, Signal.Fail, GuidedLessonPhase.Failed);
            // A confirmed undo can resume either side from any prior phase, including terminal phases.
            Add(phase, Signal.AwaitLearner, GuidedLessonPhase.ReadyForPlayerMove); Add(phase, Signal.AwaitReply, GuidedLessonPhase.WaitingForOpponentReply);
        }
        foreach (var phase in new[] { GuidedLessonPhase.LoadingSetup, GuidedLessonPhase.ShowingExplanation, GuidedLessonPhase.WaitingForMoveResult, GuidedLessonPhase.WaitingForOpponentReply })
            Add(phase, Signal.Explain, GuidedLessonPhase.ShowingExplanation);
        Add(GuidedLessonPhase.ReadyForPlayerMove, Signal.Submit, GuidedLessonPhase.WaitingForMoveResult);
        foreach (var phase in new[] { GuidedLessonPhase.ShowingExplanation, GuidedLessonPhase.WaitingForMoveResult, GuidedLessonPhase.WaitingForOpponentReply })
            Add(phase, Signal.Complete, GuidedLessonPhase.Completed);
        return new((int)GuidedLessonPhase.Failed + 1, (int)Signal.Reset + 1, transitions.ToArray(), new StateId((int)GuidedLessonPhase.Inactive));
    }
}
