using System.Text;

namespace Bezoro.Core.CodeGen;

/// <summary>
///     Provides methods for generating structured, indented code with automatic "using" header collection and scope
///     management.
/// </summary>
public class CodeWriter
{
	private const    string          INDENT_STRING = "    "; // 4 spaces
	private readonly HashSet<string> _usings       = new();

	private readonly StringBuilder _builder   = new();
	private          bool          _isNewLine = true;

	private int _indentLevel;
	private ScopeGuard? _scope;

	/// <summary>
	///     Returns the generated code as a string, including using statements and a file header.
	/// </summary>
	public override string ToString()
	{
		var finalBuilder = new StringBuilder();

		// Write file header
		finalBuilder.AppendLine("// This file is auto-generated. Do not modify.");
		finalBuilder.AppendLine();

		// Write usings
		foreach (string? @using in _usings.OrderBy(x => x, StringComparer.Ordinal)) finalBuilder.AppendLine($"using {@using};");

		if (_usings.Count != 0) finalBuilder.AppendLine();

		// Write main content
		finalBuilder.Append(_builder.ToString());
		return finalBuilder.ToString();
	}

	/// <summary>
	///     Adds a using directive for the specified namespace.
	/// </summary>
	/// <param name="nameSpace">The namespace to add as a using directive.</param>
	/// <returns>This <see cref="CodeWriter" /> instance for chaining.</returns>
	public CodeWriter AddUsing(string nameSpace)
	{
		if (string.IsNullOrWhiteSpace(nameSpace)) throw new ArgumentException("A namespace is required.", nameof(nameSpace));
		_usings.Add(nameSpace);
		return this;
	}

	/// <summary>
	///     Adds multiple using directives for the specified namespaces.
	/// </summary>
	/// <param name="namespaces">The collection of namespaces to add as using directives.</param>
	/// <returns>This <see cref="CodeWriter" /> instance for chaining.</returns>
	public CodeWriter AddUsingRange(IEnumerable<string> namespaces)
	{
		if (namespaces is null) throw new ArgumentNullException(nameof(namespaces));
		foreach (string? @namespace in namespaces) AddUsing(@namespace);
		return this;
	}

	/// <summary>
	///     Writes the specified text, applying indentation if it is the beginning of a new line.
	/// </summary>
	/// <param name="text">The text to write.</param>
	/// <returns>This <see cref="CodeWriter" /> instance for chaining.</returns>
	public CodeWriter Write(string text)
	{
		if (_isNewLine)
		{
			WriteIndent();
			_isNewLine = false;
		}

		_builder.Append(text);
		return this;
	}

	/// <summary>
	///     Writes a line of text with the current indentation, or a blank line if the input is empty.
	/// </summary>
	/// <param name="line">The line to write.</param>
	/// <returns>This <see cref="CodeWriter" /> instance for chaining.</returns>
	public CodeWriter WriteLine(string line = "")
	{
		if (!string.IsNullOrEmpty(line))
		{
			if (_isNewLine) WriteIndent();
			_builder.AppendLine(line);
		}
		else
		{
			_builder.AppendLine();
		}

		_isNewLine = true;
		return this;
	}

	/// <summary>
	///     Begins a new code scope, optionally starting with a declaration (e.g., an if/for/class signature).
	/// </summary>
	/// <param name="declaration">The declaration line to write before the opening brace, or null.</param>
	/// <returns>An <see cref="IDisposable" /> that will end the scope upon disposal.</returns>
	public IDisposable BeginScope(string? declaration = null)
	{
		if (declaration != null) WriteLine(declaration);

		WriteLine("{");
		_indentLevel++;
		_scope = new ScopeGuard(this, _scope);
		return _scope;
	}

	/// <summary>
	///     Ends the current code scope, writing the closing brace or specified closer.
	/// </summary>
	/// <param name="closer">The string to write as the closer, typically "}".</param>
	public void EndScope(string closer = "}")
	{
		if (_scope is null) throw new InvalidOperationException("There is no open scope to close.");
		_scope.Close();
		_scope = _scope.Parent;
		_indentLevel--;
		WriteLine(closer);
	}

	/// <summary>
	///     Appends indentation to the output according to the current scope level.
	/// </summary>
	private void WriteIndent()
	{
		for (var i = 0; i < _indentLevel; i++) _builder.Append(INDENT_STRING);
	}

	/// <summary>
	///     Helper type for automatically closing scopes (writes ending '}' when disposed).
	/// </summary>
	/// <remarks>
	///     Constructs a new <see cref="ScopeGuard" /> for the specified writer.
	/// </remarks>
	/// <param name="writer">The writer for which to manage the scope.</param>
	/// <param name="parent">The containing scope, if any.</param>
	private sealed class ScopeGuard(CodeWriter writer, ScopeGuard? parent) : IDisposable
	{
		private readonly CodeWriter _writer = writer;
		private bool _closed;
		internal ScopeGuard? Parent { get; } = parent;
		internal void Close() => _closed = true;

		/// <summary>
		///     Ends the current scope on the writer when disposed.
		/// </summary>
		public void Dispose()
		{
			if (_closed) return;
			if (!ReferenceEquals(_writer._scope, this))
				throw new InvalidOperationException("Close nested scopes before their containing scope.");
			_writer.EndScope();
		}
	}
}
