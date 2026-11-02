namespace Bezoro.Chess.Networking;

/// <summary>The authenticated participant's assigned seat. Spectators cannot submit commands.</summary>
public enum ChessPlayerSeat
{
    /// <summary>The player controlling the white pieces.</summary>
    White,
    /// <summary>The player controlling the black pieces.</summary>
    Black
}
