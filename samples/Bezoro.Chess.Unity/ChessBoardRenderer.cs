using Bezoro.Chess.Presentation;
using Bezoro.Chess.UCI.Protocol.API.Types;
using UnityEngine;

namespace Bezoro.Chess.UnitySample
{
    /// <summary>
    /// Attach a concrete sprite or model renderer to a separate scene object. Reuse its cells/pieces;
    /// asset loading, animation timing and renderer-specific hit testing belong to that implementation.
    /// </summary>
    public abstract class ChessBoardRenderer : MonoBehaviour
    {
        public abstract void Render(
            in ChessBoardPresentation board,
            ChessHighlightPalette palette,
            ChessBoardOrientation orientation,
            string pieceSetAssetKey);

        /// <summary>
        /// Called after reconciliation for a canonical move, undo or load event. The event identifies
        /// forward animation versus restoration; never infer this only from the last-move marker.
        /// </summary>
        public virtual void Animate(in PlayableMatchEvent matchEvent) { }
    }
}
