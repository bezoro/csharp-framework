using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Study;

/// <summary>A position in one study. Shared prefixes are stored once; the first continuation is the main line.</summary>
public sealed class StudyNode
{
    private List<StudyNode>? _variations;
    internal StudyNode(ChessStudy owner, StudyNode? parent, Fen position, string? move, string? san)
    {
        Owner = owner;
        Parent = parent;
        Position = position;
        Move = move;
        San = san;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    internal ChessStudy Owner { get; }
    /// <summary>Gets the previous position, or null at the starting position.</summary>
    public StudyNode? Parent { get; }
    /// <summary>Gets the complete position, ready for immediate navigation without replay.</summary>
    public Fen Position { get; }
    /// <summary>Gets the UCI move leading here, or null at the root.</summary>
    public string? Move { get; }
    /// <summary>Gets the canonical SAN move leading here, or null at the root.</summary>
    public string? San { get; }
    /// <summary>Gets the number of moves from the root.</summary>
    public int Depth { get; }
    /// <summary>Gets the comment attached to this position.</summary>
    public string Comment { get; internal set; } = string.Empty;
    /// <summary>Gets a comment before this move, including a variation's introductory comment.</summary>
    public string LeadingComment { get; internal set; } = string.Empty;
    /// <summary>Gets the number of alternative continuations, including the main line at index zero.</summary>
    public int VariationCount => _variations?.Count ?? 0;
    /// <summary>Gets a continuation. No collection or enumerator is allocated.</summary>
    public StudyNode GetVariation(int index) => _variations is null
        ? throw new ArgumentOutOfRangeException(nameof(index)) : _variations[index];

    internal void Add(StudyNode node) => (_variations ??= new List<StudyNode>(1)).Add(node);
    internal List<byte>? Glyphs;
    /// <summary>Gets the number of numeric annotation glyphs attached to the move.</summary>
    public int AnnotationCount => Glyphs?.Count ?? 0;
    /// <summary>Gets a PGN numeric annotation glyph (0–255).</summary>
    public byte GetAnnotation(int index) => Glyphs is null
        ? throw new ArgumentOutOfRangeException(nameof(index)) : Glyphs[index];
}
