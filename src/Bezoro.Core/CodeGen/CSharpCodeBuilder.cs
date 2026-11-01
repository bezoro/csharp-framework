using System.Text;

namespace Bezoro.Core.CodeGen;

/// <summary>
///     Provides higher-level programmatic construction and composition of C# code constructs, using
///     <see cref="CodeWriter" /> underneath.
/// </summary>
public class CSharpCodeBuilder
{
	private readonly CodeWriter    _writer         = new();
	private readonly Stack<string> _namespaceStack = new();
	private          bool          _isInClass;

	/// <summary>
	///     Adds a field declaration to the current class.
	/// </summary>
	/// <param name="name">The name of the field.</param>
	/// <param name="type">The type of the field.</param>
	/// <param name="accessibility">The accessibility modifier (e.g., private/protected/public).</param>
	/// <param name="isReadonly">Whether the field is readonly.</param>
	/// <param name="initialValue">Optional initializer value as code.</param>
	/// <returns>This <see cref="CSharpCodeBuilder" /> instance for chaining.</returns>
	/// <exception cref="InvalidOperationException">Thrown if not in a class definition.</exception>
	public CSharpCodeBuilder AddField(
		string  name,
		string  type,
		string  accessibility = "private",
		bool    isReadonly    = false,
		string? initialValue  = null
	)
	{
		if (!_isInClass) throw new InvalidOperationException("Must be in a class to add a field");

		var declaration = new StringBuilder();
		declaration.Append($"{accessibility} ");

		if (isReadonly) declaration.Append("readonly ");

		declaration.Append($"{type} {name}");

		if (initialValue != null) declaration.Append($" = {initialValue}");

		declaration.Append(";");

		_writer.WriteLine(declaration.ToString());
		return this;
	}

	/// <summary>
	///     Adds a method declaration to the current class.
	/// </summary>
	/// <param name="name">The method name.</param>
	/// <param name="returnType">The return type.</param>
	/// <param name="accessibility">Accessibility modifier.</param>
	/// <param name="isStatic">Whether the method is static.</param>
	/// <param name="parameters">Parameter list as code fragments.</param>
	/// <param name="bodyWriter">Callback that writes the method body.</param>
	/// <param name="attributes">Optional attributes for the method.</param>
	/// <returns>This <see cref="CSharpCodeBuilder" /> instance for chaining.</returns>
	/// <exception cref="InvalidOperationException">Thrown if not in a class definition.</exception>
	public CSharpCodeBuilder AddMethod(
		string              name,
		string              returnType    = "void",
		string              accessibility = "public",
		bool                isStatic      = false,
		string[]?           parameters    = null,
		Action<CodeWriter>? bodyWriter    = null,
		string[]?           attributes    = null
	)
	{
		if (!_isInClass) throw new InvalidOperationException("Must be in a class to add a method");

		// Write attributes
		if (attributes != null)
			foreach (string attribute in attributes)
				_writer.WriteLine($"[{attribute}]");

		var declaration = new StringBuilder();
		declaration.Append($"{accessibility} ");

		if (isStatic) declaration.Append("static ");

		declaration.Append($"{returnType} {name}(");

		if (parameters is { Length: > 0 }) declaration.Append(string.Join(", ", parameters));

		declaration.Append(")");
		_writer.WriteLine(declaration.ToString());

		using (_writer.BeginScope())
		{
			bodyWriter?.Invoke(_writer);
		}

		return this;
	}

	/// <summary>
	///     Adds a property declaration to the current class.
	/// </summary>
	/// <param name="name">The property name.</param>
	/// <param name="type">The type of the property.</param>
	/// <param name="accessibility">Accessibility modifier.</param>
	/// <param name="hasGetter">Whether to include a getter.</param>
	/// <param name="hasSetter">Whether to include a setter.</param>
	/// <param name="setterAccessibility">Optionally specify accessor modifier for setter.</param>
	/// <returns>This <see cref="CSharpCodeBuilder" /> instance for chaining.</returns>
	/// <exception cref="InvalidOperationException">Thrown if not in a class definition.</exception>
	/// <exception cref="ArgumentException">Thrown if neither accessor is present.</exception>
	public CSharpCodeBuilder AddProperty(
		string  name,
		string  type,
		string  accessibility       = "public",
		bool    hasGetter           = true,
		bool    hasSetter           = true,
		string? setterAccessibility = null
	)
	{
		if (!_isInClass) throw new InvalidOperationException("Must be in a class to add a property");

		var declaration = $"{accessibility} {type} {name}";

		if (!hasGetter && !hasSetter) throw new ArgumentException("Property must have at least a getter or setter");

		_writer.Write(declaration);

		using (_writer.BeginScope(" {"))
		{
			if (hasGetter) _writer.WriteLine("get;");

			if (hasSetter) _writer.WriteLine($"{setterAccessibility ?? ""} set;".TrimStart());
		}

		return this;
	}

	/// <summary>
	///     Adds a using directive for the specified namespace.
	/// </summary>
	/// <param name="nameSpace">The namespace to add as a using directive.</param>
	/// <returns>This <see cref="CSharpCodeBuilder" /> instance for chaining.</returns>
	public CSharpCodeBuilder AddUsing(string nameSpace)
	{
		_writer.AddUsing(nameSpace);
		return this;
	}

	/// <summary>
	///     Begins a class definition, writing its declaration and opening brace.
	/// </summary>
	/// <param name="className">The class name.</param>
	/// <param name="accessibility">The accessibility of the class.</param>
	/// <param name="isStatic">Whether the class is static.</param>
	/// <param name="baseClass">Optional base class name.</param>
	/// <param name="interfaces">List of implemented interfaces.</param>
	/// <returns>This <see cref="CSharpCodeBuilder" /> instance for chaining.</returns>
	/// <exception cref="InvalidOperationException">Thrown if already in a class definition.</exception>
	public CSharpCodeBuilder BeginClass(
		string    className,
		string    accessibility = "public",
		bool      isStatic      = false,
		string?   baseClass     = null,
		string[]? interfaces    = null
	)
	{
		if (_isInClass) throw new InvalidOperationException("Already in a class definition");

		var declaration = new StringBuilder();
		declaration.Append($"{accessibility} ");
		if (isStatic) declaration.Append("static ");

		declaration.Append($"class {className}");
		if (baseClass != null) declaration.Append($" : {baseClass}");

		if (interfaces is { Length: > 0 })
		{
			declaration.Append(baseClass == null ? " : " : ", ");
			declaration.Append(string.Join(", ", interfaces));
		}

		_writer.WriteLine(declaration.ToString());
		_writer.BeginScope();
		_isInClass = true;
		return this;
	}

	/// <summary>
	///     Begins a namespace block.
	/// </summary>
	/// <param name="nameSpace">The name of the namespace.</param>
	/// <returns>This <see cref="CSharpCodeBuilder" /> instance for chaining.</returns>
	public CSharpCodeBuilder BeginNamespace(string nameSpace)
	{
		if (_isInClass) throw new InvalidOperationException("A namespace cannot begin inside a class.");
		_namespaceStack.Push(nameSpace);
		_writer.WriteLine($"namespace {nameSpace}");
		_writer.BeginScope();
		return this;
	}

	/// <summary>
	///     Ends the current class definition.
	/// </summary>
	/// <returns>This <see cref="CSharpCodeBuilder" /> instance for chaining.</returns>
	/// <exception cref="InvalidOperationException">Thrown if not in a class definition.</exception>
	public CSharpCodeBuilder EndClass()
	{
		if (!_isInClass) throw new InvalidOperationException("Not in a class definition");

		_writer.EndScope();
		_isInClass = false;
		return this;
	}

	/// <summary>
	///     Ends the most recent namespace block.
	/// </summary>
	/// <returns>This <see cref="CSharpCodeBuilder" /> instance for chaining.</returns>
	/// <exception cref="InvalidOperationException">Thrown if no namespace is being defined.</exception>
	public CSharpCodeBuilder EndNamespace()
	{
		if (_isInClass) throw new InvalidOperationException("End the class before its containing namespace.");
		if (_namespaceStack.Count == 0) throw new InvalidOperationException("No namespace to end");

		_namespaceStack.Pop();
		_writer.EndScope();
		return this;
	}

	/// <summary>
	///     Generates the code accumulated in the builder as a string.
	/// </summary>
	/// <returns>The generated code as a string.</returns>
	/// <exception cref="InvalidOperationException">Thrown if there are unclosed namespaces or classes.</exception>
	public string Generate()
	{
		if (_namespaceStack.Count > 0) throw new InvalidOperationException("Unclosed namespace definitions");

		if (_isInClass) throw new InvalidOperationException("Unclosed class definition");

		return _writer.ToString();
	}
}
