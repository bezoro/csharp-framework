using System.Text;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Bezoro.ECS.SourceGen.Generators;

internal static class GeneratedNames
{
	public static string Identifier(string name) => "@" + name;

	public static string UniqueName(string name)
	{
		var result = new StringBuilder(name.Length);
		foreach (char character in name)
		{
			if (char.IsLetterOrDigit(character)) result.Append(character);
			else result.Append('_').Append(((int)character).ToString("X4"));
		}
		return result.ToString();
	}

	public static bool IsOpenType(INamedTypeSymbol type)
	{
		for (var current = type; current is not null; current = current.ContainingType)
			if (current.Arity != 0) return true;
		return false;
	}

	public static bool IsAccessible(INamedTypeSymbol type)
	{
		for (var current = type; current is not null; current = current.ContainingType)
			if (current.IsFileLocal || current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
				return false;
		return true;
	}

	public static ITypeParameterSymbol[] TypeParameters(INamedTypeSymbol type)
	{
		var owners = new Stack<INamedTypeSymbol>();
		for (var current = type; current is not null; current = current.ContainingType) owners.Push(current);
		return owners.SelectMany(static owner => owner.TypeParameters).ToArray();
	}

	public static string[] Constraints(ITypeParameterSymbol[] parameters)
	{
		var result = new List<string>();
		foreach (var parameter in parameters)
		{
			var constraints = new List<string>();
			if (parameter.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
			else if (parameter.HasValueTypeConstraint) constraints.Add("struct");
			else if (parameter.HasReferenceTypeConstraint)
				constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
			else if (parameter.HasNotNullConstraint) constraints.Add("notnull");
			constraints.AddRange(parameter.ConstraintTypes.Select(static constraint => constraint.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
			if (parameter.HasConstructorConstraint) constraints.Add("new()");
			if (constraints.Count > 0)
				result.Add("where " + Identifier(parameter.Name) + " : " + string.Join(", ", constraints));
		}
		return result.ToArray();
	}
}
