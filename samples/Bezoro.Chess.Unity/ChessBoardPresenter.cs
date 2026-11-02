using System;
using Bezoro.Chess.Presentation;
using Bezoro.Chess.UCI.Protocol.API.Types;
using UnityEngine;
using FrameworkColor = Bezoro.Core.Types.Color;

namespace Bezoro.Chess.UnitySample
{
    /// <summary>
    /// Event-driven view adapter. A separate local/network controller owns the match and calls Present after
    /// authoritative changes. This component does not use Update, run engines, mutate clocks or accept unvalidated moves.
    /// </summary>
    public sealed class ChessBoardPresenter : MonoBehaviour
    {
        [SerializeField] private ChessBoardRenderer twoDimensionalRenderer = null;
        [SerializeField] private ChessBoardRenderer threeDimensionalRenderer = null;
        [SerializeField] private ChessBoardVisualMode visualMode;
        [SerializeField] private ChessBoardOrientation orientation;
        [SerializeField] private string skinId = "classic";
        [SerializeField] private string spriteSetAssetKey = "skins/classic/sprites";
        [SerializeField] private string modelSetAssetKey = "skins/classic/models";
        [SerializeField] private Color32 selected = new Color32(255, 215, 0, 150);
        [SerializeField] private Color32 legalMove = new Color32(80, 180, 100, 110);
        [SerializeField] private Color32 legalCapture = new Color32(240, 130, 60, 150);
        [SerializeField] private Color32 lastMove = new Color32(190, 210, 60, 110);
        [SerializeField] private Color32 capture = new Color32(230, 100, 70, 130);
        [SerializeField] private Color32 castlingRook = new Color32(100, 160, 230, 110);
        [SerializeField] private Color32 promotion = new Color32(175, 100, 235, 160);
        [SerializeField] private Color32 check = new Color32(240, 70, 60, 170);
        [SerializeField] private Color32 checkmate = new Color32(180, 20, 30, 210);

        private PlayableMatchState state;
        private ChessBoardPresentation board;
        private ChessHighlightPalette palette;
        private ChessPieceSkin skin;
        private ChessSquare? selectedSquare;
        private bool hasPosition;

        /// <summary>Submit through the game's local or network command controller.</summary>
        public event Action<string> MoveRequested;

        /// <summary>Submit the chosen promotion suffix through the same command controller.</summary>
        public event Action<char> PromotionChosen;

        /// <summary>Display an application-owned promotion UI when this becomes non-null.</summary>
        public event Action<PendingPromotionRequest?> PromotionChanged;

        /// <summary>The controller may disable input while awaiting authority or during an animation.</summary>
        public bool InteractionEnabled { get; set; } = true;

        private void Awake() => RebuildAppearance();

        /// <summary>
        /// Call on Unity's main thread after a move, undo, load, result change or authoritative network snapshot.
        /// Pass a canonical event only once when its corresponding transition should animate.
        /// </summary>
        public void Present(in PlayableMatchState authoritativeState, PlayableMatchEvent? animation = null)
        {
            var previousPromotion = hasPosition ? state.PendingPromotion : null;
            state = authoritativeState;
            hasPosition = true;
            Reconcile();
            if (animation.HasValue && ActiveRenderer != null)
            {
                var value = animation.Value;
                ActiveRenderer.Animate(in value);
            }
            if (previousPromotion != state.PendingPromotion)
                PromotionChanged?.Invoke(state.PendingPromotion);
        }

        /// <summary>Connect a renderer's hit test to these bottom-left-origin cell coordinates.</summary>
        public void SelectCell(int column, int row)
        {
            if (!hasPosition || !InteractionEnabled || state.Result.IsTerminal || state.PendingPromotion.HasValue)
                return;
            var square = ChessBoardCoordinates.FromDisplayCell(column, row, orientation);
            var highlights = board.GetHighlights(square);
            if (selectedSquare.HasValue &&
                (highlights & (ChessSquareHighlights.LegalMove | ChessSquareHighlights.LegalCapture)) != 0)
            {
                string move = UciCoordinateMove.Create(selectedSquare.Value, square).Notation;
                selectedSquare = null;
                Reconcile();
                MoveRequested?.Invoke(move);
                return;
            }
            selectedSquare = selectedSquare == square ? null : square;
            Reconcile();
        }

        /// <summary>Connect the promotion panel's four buttons using q, r, b or n.</summary>
        public void ChoosePromotion(char piece)
        {
            if (!hasPosition || !InteractionEnabled || !state.PendingPromotion.HasValue) return;
            char normalized = char.ToLowerInvariant(piece);
            foreach (char allowed in state.PendingPromotion.Value.AllowedPromotionPieces)
                if (allowed == normalized)
                {
                    PromotionChosen?.Invoke(normalized);
                    return;
                }
            throw new ArgumentException("This piece is not an allowed promotion choice.", nameof(piece));
        }

        /// <summary>Changing the renderer never changes or reloads the match.</summary>
        public void SetVisualMode(ChessBoardVisualMode mode)
        {
            if (mode != ChessBoardVisualMode.TwoDimensional && mode != ChessBoardVisualMode.ThreeDimensional)
                throw new ArgumentOutOfRangeException(nameof(mode));
            visualMode = mode;
            Reconcile();
        }

        public void SetOrientation(ChessBoardOrientation value)
        {
            if (value != ChessBoardOrientation.White && value != ChessBoardOrientation.Black)
                throw new ArgumentOutOfRangeException(nameof(value));
            orientation = value;
            Reconcile();
        }

        /// <summary>Call only after the player profile/store has authorized ownership of this skin.</summary>
        public void SetSkin(ChessPieceSkin value)
        {
            value.GetAssetKey(visualMode); // Reject an uninitialized descriptor.
            skin = value;
            Reconcile();
        }

        public void SetPalette(ChessHighlightPalette value)
        {
            palette = value ?? throw new ArgumentNullException(nameof(value));
            Reconcile();
        }

        private ChessBoardRenderer ActiveRenderer => visualMode == ChessBoardVisualMode.TwoDimensional
            ? twoDimensionalRenderer
            : threeDimensionalRenderer;

        private void Reconcile()
        {
            if (palette == null) RebuildAppearance();
            if (hasPosition)
            {
                board = ChessBoardPresentation.FromMatch(in state, selectedSquare);
                selectedSquare = board.SelectedSquare;
                if (ActiveRenderer != null)
                    ActiveRenderer.Render(in board, palette, orientation, skin.GetAssetKey(visualMode));
            }
            // Prepare the new view before showing it. Assign distinct child objects in the inspector.
            if (twoDimensionalRenderer != null)
                twoDimensionalRenderer.gameObject.SetActive(visualMode == ChessBoardVisualMode.TwoDimensional);
            if (threeDimensionalRenderer != null)
                threeDimensionalRenderer.gameObject.SetActive(visualMode == ChessBoardVisualMode.ThreeDimensional);
        }

        private void RebuildAppearance()
        {
            palette = new ChessHighlightPalette(ToColor(selected), ToColor(legalMove), ToColor(legalCapture),
                ToColor(lastMove), ToColor(capture), ToColor(castlingRook), ToColor(promotion),
                ToColor(check), ToColor(checkmate));
            skin = new ChessPieceSkin(skinId, spriteSetAssetKey, modelSetAssetKey);
        }

        private static FrameworkColor ToColor(Color32 color) => new FrameworkColor(color.r, color.g, color.b, color.a);
    }
}
