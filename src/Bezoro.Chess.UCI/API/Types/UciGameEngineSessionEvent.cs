using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.API.Types;

/// <summary>
///     Compact typed event payload emitted by <see cref="UciGameEngineSession.EventPublished" />.
/// </summary>
public readonly record struct UciGameEngineSessionEvent(
	UciGameEngineSessionEventKind Kind,
	DateTimeOffset Timestamp,
	UciState? State = null,
	GameStartedEvent? GameStarted = null,
	PositionLoadedEvent? PositionLoaded = null,
	GameMoveEvent? Move = null,
	MoveUndoneEvent? MoveUndone = null,
	PromotionRequiredEvent? PromotionRequired = null,
	PromotionChosenEvent? PromotionChosen = null,
	TurnChangedEvent? TurnChanged = null,
	IllegalMoveRejectedEvent? IllegalMoveRejected = null,
	PrincipalVariation? Evaluation = null,
	Move? ClassifiedMove = null,
	Exception? Exception = null,
	PromotionCanceledEvent? PromotionCanceled = null)
{
	/// <summary>Gets the identity-bearing terminal payload without changing the existing constructor or deconstruction contract.</summary>
	public GameFinishedEvent? GameFinished { get; init; }

	/// <summary>Creates an identity-bearing terminal match event.</summary>
	public static UciGameEngineSessionEvent ForGameFinished(GameFinishedEvent payload) =>
		new(UciGameEngineSessionEventKind.GameFinished, DateTimeOffset.UtcNow, State: payload.State) { GameFinished = payload };

	/// <summary>Creates a pending-promotion cancellation event.</summary>
	public static UciGameEngineSessionEvent ForPromotionCanceled(PromotionCanceledEvent payload) =>
		new(UciGameEngineSessionEventKind.PromotionCanceled, payload.TimestampUtc, PromotionCanceled: payload);

	/// <summary>Creates an event without a specialized payload.</summary>
	public static UciGameEngineSessionEvent Create(UciGameEngineSessionEventKind kind) =>
		new(kind, DateTimeOffset.UtcNow);

	/// <summary>Creates a state-carrying event.</summary>
	public static UciGameEngineSessionEvent ForState(UciGameEngineSessionEventKind kind, UciState state) =>
		new(kind, DateTimeOffset.UtcNow, State: state);

	/// <summary>Creates a game-started event.</summary>
	public static UciGameEngineSessionEvent ForGameStarted(GameStartedEvent gameStarted) =>
		new(UciGameEngineSessionEventKind.GameStarted, DateTimeOffset.UtcNow, GameStarted: gameStarted);

	/// <summary>Creates a position-loaded event.</summary>
	public static UciGameEngineSessionEvent ForPositionLoaded(PositionLoadedEvent positionLoaded) =>
		new(UciGameEngineSessionEventKind.PositionLoaded, DateTimeOffset.UtcNow, PositionLoaded: positionLoaded);

	/// <summary>Creates a move event.</summary>
	public static UciGameEngineSessionEvent ForMove(UciGameEngineSessionEventKind kind, GameMoveEvent move) =>
		new(kind, DateTimeOffset.UtcNow, Move: move);

	/// <summary>Creates a move-undone event.</summary>
	public static UciGameEngineSessionEvent ForMoveUndone(MoveUndoneEvent moveUndone) =>
		new(UciGameEngineSessionEventKind.MoveUndone, DateTimeOffset.UtcNow, MoveUndone: moveUndone);

	/// <summary>Creates a promotion-required event.</summary>
	public static UciGameEngineSessionEvent ForPromotionRequired(PromotionRequiredEvent promotionRequired) =>
		new(
			UciGameEngineSessionEventKind.PromotionRequired,
			DateTimeOffset.UtcNow,
			PromotionRequired: promotionRequired
		);

	/// <summary>Creates a promotion-chosen event.</summary>
	public static UciGameEngineSessionEvent ForPromotionChosen(PromotionChosenEvent promotionChosen) =>
		new(UciGameEngineSessionEventKind.PromotionChosen, DateTimeOffset.UtcNow, PromotionChosen: promotionChosen);

	/// <summary>Creates a turn-changed event.</summary>
	public static UciGameEngineSessionEvent ForTurnChanged(TurnChangedEvent turnChanged) =>
		new(UciGameEngineSessionEventKind.TurnChanged, DateTimeOffset.UtcNow, TurnChanged: turnChanged);

	/// <summary>Creates an illegal-move event.</summary>
	public static UciGameEngineSessionEvent ForIllegalMove(IllegalMoveRejectedEvent illegalMove) =>
		new(UciGameEngineSessionEventKind.IllegalMoveRejected, DateTimeOffset.UtcNow, IllegalMoveRejected: illegalMove);

	/// <summary>Creates an evaluation event.</summary>
	public static UciGameEngineSessionEvent ForEvaluation(PrincipalVariation evaluation) =>
		new(UciGameEngineSessionEventKind.EvaluationChanged, DateTimeOffset.UtcNow, Evaluation: evaluation);

	/// <summary>Creates a move-classification event.</summary>
	public static UciGameEngineSessionEvent ForClassifiedMove(Move classifiedMove) =>
		new(UciGameEngineSessionEventKind.MoveClassified, DateTimeOffset.UtcNow, ClassifiedMove: classifiedMove);

	/// <summary>Creates an error event.</summary>
	public static UciGameEngineSessionEvent ForError(Exception exception) =>
		new(UciGameEngineSessionEventKind.Error, DateTimeOffset.UtcNow, Exception: exception);
}
