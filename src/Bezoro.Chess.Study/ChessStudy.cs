using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Study;

/// <summary>
/// A caller-owned, single-threaded analysis tree. Undo retains continuations; playing another move creates a variation.
/// It starts no engine, clock, task or event loop. Study navigation never changes a live match.
/// </summary>
public sealed class ChessStudy
{
    private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);
    private readonly ReadOnlyDictionary<string, string> _readOnlyTags;
    private readonly int _maxNodes;
    /// <summary>Creates a study with an explicit storage limit (including the root).</summary>
    public ChessStudy(Fen? startingPosition = null, int maxNodes = 4096)
    {
        if (maxNodes < 1) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        Fen position = startingPosition ?? Fen.Default;
        if (position.PiecePlacement is null) throw new ArgumentException("A parsed FEN is required.", nameof(startingPosition));
        position.ValidateForPlay();
        _maxNodes = maxNodes;
        Root = Current = new StudyNode(this, null, position, null, null);
        _readOnlyTags = new ReadOnlyDictionary<string, string>(_tags);
    }

    /// <summary>Gets the starting position.</summary>
    public StudyNode Root { get; }
    /// <summary>Gets the selected position.</summary>
    public StudyNode Current { get; private set; }
    /// <summary>Gets the stored node count, including the root.</summary>
    public int NodeCount { get; private set; } = 1;
    /// <summary>Gets game metadata. Use SetTag to change it.</summary>
    public IReadOnlyDictionary<string, string> Tags => _readOnlyTags;
    /// <summary>Gets the recorded PGN result, independently of study variations. Defaults to unfinished.</summary>
    public string Result => _tags.TryGetValue("Result", out string? result) ? result : "*";

    /// <summary>Plays a legal move or selects its existing continuation. Failure leaves the tree and cursor unchanged.</summary>
    public StudyNode Play(string move) => PlayCore(move, reuseContinuation: true);

    internal StudyNode ImportSan(string san) => PlayCore(ChessNotation.ParseSan(Current.Position, san), reuseContinuation: false);

    private StudyNode PlayCore(string move, bool reuseContinuation)
    {
        if (move is null) throw new ArgumentNullException(nameof(move));
        string normalized = move.Trim().ToLowerInvariant();
        for (int i = 0; reuseContinuation && i < Current.VariationCount; i++)
        {
            StudyNode child = Current.GetVariation(i);
            if (child.Move == normalized) return Current = child;
        }
        if (NodeCount >= _maxNodes) throw new InvalidOperationException("The study node limit has been reached.");
        string san = ChessNotation.ToSan(Current.Position, normalized);
        Fen next = Current.Position.ApplyMove(normalized);
        var node = new StudyNode(this, Current, next, normalized, san);
        Current.Add(node);
        NodeCount++;
        return Current = node;
    }

    /// <summary>Resolves and plays a legal SAN move.</summary>
    public StudyNode PlaySan(string san) => Play(ChessNotation.ParseSan(Current.Position, san));
    /// <summary>Returns to the parent while preserving every continuation. Returns false at the root.</summary>
    public bool Undo()
    {
        if (Current.Parent is not { } parent) return false;
        Current = parent;
        return true;
    }
    /// <summary>Follows a stored continuation. Returns false if the selected variation does not exist.</summary>
    public bool Redo(int variationIndex = 0)
    {
        if ((uint)variationIndex >= (uint)Current.VariationCount) return false;
        Current = Current.GetVariation(variationIndex);
        return true;
    }
    /// <summary>Selects a position owned by this study in constant time.</summary>
    public void GoTo(StudyNode node)
    {
        ValidateOwner(node);
        Current = node;
    }
    /// <summary>Attaches a comment. Export uses semicolon comments when its text contains braces.</summary>
    public void SetComment(StudyNode node, string comment)
    {
        ValidateOwner(node);
        if (comment is null) throw new ArgumentNullException(nameof(comment));
        if (comment.IndexOf('\0') >= 0) throw new ArgumentException("Comments cannot contain NUL.", nameof(comment));
        node.Comment = comment;
    }
    /// <summary>Attaches an introduction to an alternative's first move, preserving its scope during PGN interchange.</summary>
    public void SetLeadingComment(StudyNode node, string comment)
    {
        ValidateOwner(node);
        if (node.Parent is null || ReferenceEquals(node.Parent.GetVariation(0), node))
            throw new ArgumentException("Leading comments introduce alternative variations. Use a position comment for the main line.", nameof(node));
        if (comment is null) throw new ArgumentNullException(nameof(comment));
        if (comment.IndexOf('\0') >= 0) throw new ArgumentException("Comments cannot contain NUL.", nameof(comment));
        node.LeadingComment = comment;
    }
    /// <summary>Adds one distinct numeric annotation glyph to a move (root annotations are not allowed).</summary>
    public void AddAnnotation(StudyNode node, byte glyph)
    {
        ValidateOwner(node);
        if (node.Parent is null) throw new ArgumentException("The root has no move to annotate.", nameof(node));
        var glyphs = node.Glyphs ??= new List<byte>(1);
        if (!glyphs.Contains(glyph)) glyphs.Add(glyph);
    }
    /// <summary>Sets a valid PGN tag. FEN and SetUp are generated from the actual root and cannot be overridden.</summary>
    public void SetTag(string name, string value)
    {
        if (string.IsNullOrEmpty(name) || !IsLetter(name[0]) || name.Any(c => !IsLetter(c) && !(c is >= '0' and <= '9') && c != '_'))
            throw new ArgumentException("A PGN tag name must start with a letter and contain only letters, digits or underscores.", nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value.Any(char.IsControl)) throw new ArgumentException("PGN tag values cannot contain control characters.", nameof(value));
        if (name is "FEN" or "SetUp") throw new ArgumentException("Setup tags are derived from the starting position.", nameof(name));
        if (name == "Result" && value is not ("*" or "1-0" or "0-1" or "1/2-1/2")) throw new ArgumentException("Invalid PGN result.", nameof(value));
        _tags[name] = value;
    }
    /// <summary>Copies the selected root-to-cursor move history for an explicit engine review request.</summary>
    public ImmutableArray<string> GetCurrentMoves()
    {
        var moves = ImmutableArray.CreateBuilder<string>(Current.Depth);
        moves.Count = Current.Depth;
        StudyNode node = Current;
        for (int i = moves.Count - 1; i >= 0; i--, node = node.Parent!) moves[i] = node.Move!;
        return moves.MoveToImmutable();
    }
    private void ValidateOwner(StudyNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        if (!ReferenceEquals(node.Owner, this)) throw new ArgumentException("The node belongs to a different study.", nameof(node));
    }
    private static bool IsLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
