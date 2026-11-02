using Bezoro.Chess.UCI.API;

namespace Bezoro.Chess.UCI.API.Types;

/// <summary>
///     Identifies a typed event emitted by <see cref="UciGameEngineSession" />.
/// </summary>
public enum UciGameEngineSessionEventKind
{
	/// <summary>An engine-facing operation failed.</summary>
	Error,
	/// <summary>The session is ready for use.</summary>
	Ready,
	/// <summary>A new game session started.</summary>
	GameStarted,
	/// <summary>The aggregate session state changed.</summary>
	StateChanged,
	/// <summary>The visible board position changed.</summary>
	PositionChanged,
	/// <summary>A full position was loaded.</summary>
	PositionLoaded,
	/// <summary>The legal move set changed.</summary>
	LegalMovesUpdated,
	/// <summary>The engine search lifecycle changed.</summary>
	SearchStateChanged,
	/// <summary>The principal variation evaluation changed.</summary>
	EvaluationChanged,
	/// <summary>The current best move changed.</summary>
	BestMoveChanged,
	/// <summary>A legal move classification was produced.</summary>
	MoveClassified,
	/// <summary>The current position classification run completed.</summary>
	ClassificationCompleted,
	/// <summary>The game reached a terminal state.</summary>
	GameOver,
	/// <summary>The match result changed.</summary>
	ResultChanged,
	/// <summary>A move was applied.</summary>
	MoveMade,
	/// <summary>An applied move captured a piece.</summary>
	CaptureMade,
	/// <summary>An applied move castled.</summary>
	CastlingMade,
	/// <summary>An applied move captured en passant.</summary>
	EnPassantMade,
	/// <summary>A move requires a promotion choice.</summary>
	PromotionRequired,
	/// <summary>A promotion choice was applied.</summary>
	PromotionChosen,
	/// <summary>An applied move gave check.</summary>
	Check,
	/// <summary>An applied move gave checkmate.</summary>
	Checkmate,
	/// <summary>An applied move caused stalemate.</summary>
	Stalemated,
	/// <summary>The side to move changed.</summary>
	TurnChanged,
	/// <summary>A move was rejected.</summary>
	IllegalMoveRejected,
	/// <summary>A draw offer was published.</summary>
	DrawOffered,
	/// <summary>A draw offer was declined or cleared.</summary>
	DrawDeclined,
	/// <summary>The clock was paused.</summary>
	ClockPaused,
	/// <summary>The clock was resumed.</summary>
	ClockResumed,
	/// <summary>One or more moves were undone.</summary>
	MoveUndone,
	/// <summary>An engine started thinking.</summary>
	EngineThinkingStarted,
	/// <summary>An engine stopped thinking.</summary>
	EngineThinkingStopped,
	/// <summary>The session stopped.</summary>
	Stopped,
	/// <summary>A pending promotion prompt was canceled before a move was applied.</summary>
	PromotionCanceled
}
