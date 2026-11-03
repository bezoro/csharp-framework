# Bezoro.Chess.Presentation

Optional, engine-independent board presentation for Unity, Godot and other clients. Targets .NET Standard 2.1 and .NET 10. It references the local chess protocol module and never starts Stockfish, searches moves or advances the match clock.

`ChessBoardPresentation.FromMatch` projects an existing `PlayableMatchState` into an immutable value containing piece bitboards, current selection and square annotations. Call it after a match or selection change. The same projection can drive a sprite board and a model board, so changing views does not recreate the game, change its turn or discard history. Retained projections are safe to share with readers on other threads; the mutable match owner still requires its own serialized access.

```csharp
using Bezoro.Chess.Presentation;
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Core.Types;

using var match = new UciPlayableMatchSession();
var state = match.Refresh();
ChessSquare.TryParse("e2", out var selected);
var board = ChessBoardPresentation.FromMatch(in state, selected);
var palette = new ChessHighlightPalette(
    selected: new Color(255, 210, 0, 150),
    checkmate: new Color(180, 10, 20, 220));

for (int row = 0; row < 8; row++)
for (int column = 0; column < 8; column++)
{
    var square = ChessBoardCoordinates.FromDisplayCell(
        column, row, ChessBoardOrientation.White);
    char piece = board.GetPieceSymbol(square); // '\0' means empty
    var highlights = board.GetHighlights(square);
    var rgba = palette.GetOverlay(highlights);
    // Update the renderer's existing piece and overlay objects here.
}

match.ApplyMove("e2e4");
state = match.Refresh();
board = ChessBoardPresentation.FromMatch(in state);

match.UndoMoves();
state = match.Refresh();
board = ChessBoardPresentation.FromMatch(in state);
// Pieces, last-move markers and checked king now reflect the restored position.
```

Unity converts the existing byte color with `new UnityEngine.Color32(rgba.R, rgba.G, rgba.B, rgba.A)`. Use `ChessBoardCoordinates.GetColumn/GetRow` to place a square, or `FromDisplayCell` after picking a cell. Coordinates have a bottom-left origin; a top-left UI grid must invert its row before calling `FromDisplayCell`. A renderer can apply its own world transform to the resulting grid, including perspective or camera rotation. Orientation changes display mapping only; the algebraic identity of every square remains unchanged.

## Highlights and move animations

Annotations are independent flags, so a square can carry a last-move marker and a capture or promotion marker together. The palette supplies one explicit priority order for a single overlay; renderers may instead combine flags as different shapes, rings, outlines or materials.

- Selection and legal quiet/capturing destinations use the snapshot's existing legal moves. Duplicate promotion choices become a single destination marker. Selecting an enemy or empty square produces no selection. Terminal positions expose no playable selection.
- Last move marks the primary piece's source and destination. `LastMove` also provides the original moving and captured piece symbols for animation.
- En passant marks the empty destination as a legal capture and the actual victim square after the capture.
- Castling also marks the rook's source and destination, for both sides and both directions.
- A pending promotion marks its source as selected and its destination as a promotion choice. A completed promotion retains the promotion marker and the chosen piece's correctly cased FEN symbol.
- Check is evaluated locally for the side to move, including positions loaded without any history. Checkmate highlights the mated king using the match's adjudicated result. A stalemate does not highlight a checked king.

Refresh the projection after a command, promotion choice, load or undo. It always reconciles from the supplied snapshot, rather than accumulating visual changes. Compare the previous and new position keys, and use the canonical match events to decide whether a change should animate forward, undo or load instantly. A clock-only snapshot or a selection change should not replay the last animation.

## Switching between 2D and 3D

Keep one match owner and pass its projection to each renderer. A switch can prepare the newly selected renderer from the current projection, then toggle renderer visibility. Do not call `LoadMatch`, replay moves, restart clocks or issue a new engine request when switching a view.

```csharp
var skin = new ChessPieceSkin(
    id: "marble",
    twoDimensionalAssetKey: "skins/marble/sprites",
    threeDimensionalAssetKey: "skins/marble/models");

var mode = ChessBoardVisualMode.ThreeDimensional;
string assetSetKey = skin.GetAssetKey(mode);
// Resolve the asset set through the application's asset manager.
// Its mapping uses the twelve FEN symbols: PNBRQKpnbrqk.
// Render the same 'board' value with the chosen camera and asset set.
```

`ChessPieceSkin` contains metadata only. Unity Addressables handles, sprite atlases, model prefabs, pooling, loading failures and asset release remain application-owned. Both asset keys are required so a selected piece set supports either visualization. A skin descriptor is not a purchase receipt or ownership check; the store and player profile must authorize cosmetic selection independently.

## Cost and ownership

Projection creation parses two compact piece placements at most (current and last move's parent), scans the existing legal moves for the selected source, and checks whether the active king is attacked. It does not generate a new legal-move set or resolve tactical outcomes for every candidate. Pieces are stored in seven bitboards and annotations use value fields rather than per-square objects or dictionaries. Valid snapshot projection and square queries allocate no managed memory after initialization, as covered by a warmed .NET 10 regression test; Unity Mono/IL2CPP performance still requires measurement in the target build.

Create palettes and skin descriptors when configuration changes. Reuse the renderer's 64 cells, piece instances and highlight objects; the module does not create rendering objects or require an `Update` callback. The immutable projection retains only its position key, not the match, entire move history or legal-move collection. It expects coherent snapshots produced by the match owner, including lowercase UCI history whose parent position keys contain FENs.

