using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Bezoro.ECS.SourceGen.Generators;

/// <summary>
///     Generates compiled query specifications from ECS query attributes on partial structs.
/// </summary>
[Generator]
public sealed class QuerySourceGenerator : IIncrementalGenerator
{
	private const string QUERY_ATTRIBUTE_NAME = "Bezoro.ECS.Attributes.QueryAttribute";
	private static readonly DiagnosticDescriptor InvalidDeclarationDescriptor = new(
		"BECSG002", "Unsupported query declaration",
		"Query '{0}' must be a top-level partial struct or record struct, without ref or file modifiers",
		"Usage", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor InvalidComponentDescriptor = new(
		"BECSG003", "Unsupported query component",
		"Query component '{0}' must be a closed non-nullable value type that can be stored in a world",
		"Usage", DiagnosticSeverity.Error, true);
	private static readonly DiagnosticDescriptor UnsupportedQueryAttributeDescriptor = new(
		"BECSG001",
		"Unsupported query attribute",
		"Query attribute '{0}' is not supported by generated compiled-query specs and will be ignored",
		"Usage",
		DiagnosticSeverity.Warning,
		true
	);

	/// <inheritdoc />
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var queryModels = context.SyntaxProvider.ForAttributeWithMetadataName(
									 QUERY_ATTRIBUTE_NAME,
									 static (node, _) => node is TypeDeclarationSyntax,
									 static (syntaxContext, _) =>
										 CreateModel((INamedTypeSymbol)syntaxContext.TargetSymbol)
								 )
								 .Where(static model => model is { })
								 .Select(static (model, _) => model!);

		context.RegisterSourceOutput(
			queryModels, static (productionContext, model) =>
			{
				for (var i = 0; i < model.UnsupportedAttributeNames.Length; i++)
				{
					productionContext.ReportDiagnostic(
						Diagnostic.Create(
							UnsupportedQueryAttributeDescriptor,
							model.DeclarationLocation,
							model.UnsupportedAttributeNames[i]
						)
					);
				}

				if (!model.IsPartial || model.IsNested || model.IsUnsupported)
				{
					productionContext.ReportDiagnostic(Diagnostic.Create(
						InvalidDeclarationDescriptor, model.DeclarationLocation, model.TypeName));
					return;
				}
				if (model.InvalidComponents.Length > 0)
				{
					foreach (string component in model.InvalidComponents)
						productionContext.ReportDiagnostic(Diagnostic.Create(InvalidComponentDescriptor, model.DeclarationLocation, component));
					return;
				}

				string source = BuildSource(model);
				productionContext.AddSource(model.HintName, SourceText.From(source, Encoding.UTF8));
			}
		);
	}

	private static bool IsPartial(INamedTypeSymbol symbol)
	{
		for (var i = 0; i < symbol.DeclaringSyntaxReferences.Length; i++)
		{
			if (symbol.DeclaringSyntaxReferences[i].GetSyntax() is TypeDeclarationSyntax structSyntax)
				for (var m = 0; m < structSyntax.Modifiers.Count; m++)
				{
					if (structSyntax.Modifiers[m].IsKind(SyntaxKind.PartialKeyword))
						return true;
				}
		}

		return false;
	}

	private static QueryModel? CreateModel(INamedTypeSymbol symbol)
	{
		if (symbol.TypeKind != TypeKind.Struct)
			return null;

		var all         = new HashSet<string>(StringComparer.Ordinal);
		var none        = new HashSet<string>(StringComparer.Ordinal);
		var any         = new HashSet<string>(StringComparer.Ordinal);
		var optional    = new HashSet<string>(StringComparer.Ordinal);
		var added       = new HashSet<string>(StringComparer.Ordinal);
		var changed     = new HashSet<string>(StringComparer.Ordinal);
		var unsupported = new HashSet<string>(StringComparer.Ordinal);
		var invalidComponents = new HashSet<string>(StringComparer.Ordinal);

		foreach (var attribute in symbol.GetAttributes())
		{
			var attributeClass = attribute.AttributeClass;
			if (attributeClass is null) continue;

			if (attributeClass.ContainingNamespace.ToDisplayString() != "Bezoro.ECS.Attributes")
				continue;
			foreach (var argument in attribute.ConstructorArguments)
				if (argument.Kind == TypedConstantKind.Type && argument.Value is ITypeSymbol type &&
					(!type.IsValueType || type is INamedTypeSymbol named && (named.IsRefLikeType || named.IsUnboundGenericType) ||
					 type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
					invalidComponents.Add(ToFullyQualified(type));

			if (attributeClass.Name is "AllAttribute" or "WithAttribute")
			{
				if (TryGetSingleAttributeTypeName(attribute, out var componentType))
					all.Add(componentType);
			}
			else if (attributeClass.Name is "NoneAttribute" or "WithoutAttribute")
			{
				if (TryGetSingleAttributeTypeName(attribute, out var componentType))
					none.Add(componentType);
			}
			else if (attributeClass.Name == "AnyAttribute")
			{
				if (TryGetPairAttributeTypeNames(attribute, out var firstType, out var secondType))
				{
					any.Add(firstType);
					any.Add(secondType);
				}
			}
			else if (attributeClass.Name == "AnyOfAttribute")
			{
				if (TryGetSingleAttributeTypeName(attribute, out var componentType))
					any.Add(componentType);
			}
			else if (attributeClass.Name == "OptionalAttribute")
			{
				if (TryGetSingleAttributeTypeName(attribute, out var componentType))
					optional.Add(componentType);
			}
			else if (attributeClass.Name == "ChangedAttribute")
			{
				if (TryGetSingleAttributeTypeName(attribute, out var componentType))
					changed.Add(componentType);
			}
			else if (attributeClass.Name == "AddedAttribute")
			{
				if (TryGetSingleAttributeTypeName(attribute, out var componentType))
					added.Add(componentType);
			}
			else if (attributeClass.Name != "QueryAttribute")
			{
				unsupported.Add(attributeClass.Name);
			}
		}

		string typeNamespace = symbol.ContainingNamespace.IsGlobalNamespace
								   ? string.Empty
								   : symbol.ContainingNamespace.ToDisplayString();
		string hintName = GeneratedNames.UniqueName(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) +
			".CompiledQuery.g.cs";
		string typeName = GeneratedNames.Identifier(symbol.Name);
		if (symbol.TypeParameters.Length > 0)
			typeName += "<" + string.Join(", ", symbol.TypeParameters.Select(static parameter => GeneratedNames.Identifier(parameter.Name))) + ">";

		return new(
			typeNamespace,
			typeName,
			hintName,
			all.ToArray(),
			none.ToArray(),
			any.ToArray(),
			optional.ToArray(),
			added.ToArray(),
			changed.ToArray(),
			unsupported.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
			symbol.Locations.FirstOrDefault() ?? Location.None,
			IsPartial(symbol),
			symbol.IsReadOnly,
			symbol.ContainingType is { },
			symbol.IsRefLikeType || symbol.IsFileLocal,
			symbol.IsRecord,
			invalidComponents.OrderBy(static name => name, StringComparer.Ordinal).ToArray()
		);
	}

	private static string BuildSource(QueryModel model)
	{
		var builder = new StringBuilder();
		builder.AppendLine("// <auto-generated />");

		if (!string.IsNullOrEmpty(model.Namespace))
		{
			builder.Append("namespace ").Append(model.Namespace).AppendLine(";");
			builder.AppendLine();
		}

		if (model.IsReadOnly) builder.Append("readonly ");
		builder.Append(model.IsRecord ? "partial record struct " : "partial struct ")
			   .Append(model.TypeName)
			   .AppendLine(" : global::Bezoro.ECS.Abstractions.ICompiledQuerySpec");
		builder.AppendLine("{");
		builder.AppendLine(
			"    void global::Bezoro.ECS.Abstractions.ICompiledQuerySpec.Build(ref global::Bezoro.ECS.Types.QueryBuilder builder)"
		);
		builder.AppendLine("    {");

		var sortedAll = model.All.OrderBy(static t => t, StringComparer.Ordinal).ToArray();
		for (var i = 0; i < sortedAll.Length; i++)
			builder.Append("        builder.With<").Append(sortedAll[i]).AppendLine(">();");

		var sortedNone = model.None.OrderBy(static t => t, StringComparer.Ordinal).ToArray();
		for (var i = 0; i < sortedNone.Length; i++)
			builder.Append("        builder.Without<").Append(sortedNone[i]).AppendLine(">();");

		var sortedAny = model.Any.OrderBy(static t => t, StringComparer.Ordinal).ToArray();
		for (var i = 0; i < sortedAny.Length; i++)
			builder.Append("        builder.AnyOf<").Append(sortedAny[i]).AppendLine(">();");

		var sortedOptional = model.Optional.OrderBy(static t => t, StringComparer.Ordinal).ToArray();
		for (var i = 0; i < sortedOptional.Length; i++)
			builder.Append("        builder.Optional<").Append(sortedOptional[i]).AppendLine(">();");

		var sortedChanged = model.Changed.OrderBy(static t => t, StringComparer.Ordinal).ToArray();
		for (var i = 0; i < sortedChanged.Length; i++)
			builder.Append("        builder.Changed<").Append(sortedChanged[i]).AppendLine(">();");

		var sortedAdded = model.Added.OrderBy(static t => t, StringComparer.Ordinal).ToArray();
		for (var i = 0; i < sortedAdded.Length; i++)
			builder.Append("        builder.Added<").Append(sortedAdded[i]).AppendLine(">();");

		builder.AppendLine("    }");
		builder.AppendLine("}");
		return builder.ToString();
	}

	private static string ToFullyQualified(ITypeSymbol symbol) =>
		symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

	private static bool TryGetSingleAttributeTypeName(AttributeData attribute, out string typeName)
	{
		typeName = string.Empty;
		if (TryGetSingleAttributeType(attribute, out var type))
		{
			typeName = ToFullyQualified(type);
			return true;
		}

		return false;
	}

	private static bool TryGetPairAttributeTypeNames(AttributeData attribute, out string firstType, out string secondType)
	{
		firstType = string.Empty;
		secondType = string.Empty;
		var attributeClass = attribute.AttributeClass;
		if (attributeClass is { TypeArguments.Length: 2 })
		{
			firstType = ToFullyQualified(attributeClass.TypeArguments[0]);
			secondType = ToFullyQualified(attributeClass.TypeArguments[1]);
			return true;
		}

		if (attribute.ConstructorArguments.Length == 2 &&
			attribute.ConstructorArguments[0].Kind == TypedConstantKind.Type &&
			attribute.ConstructorArguments[0].Value is ITypeSymbol firstSymbol &&
			attribute.ConstructorArguments[1].Kind == TypedConstantKind.Type &&
			attribute.ConstructorArguments[1].Value is ITypeSymbol secondSymbol)
		{
			firstType = ToFullyQualified(firstSymbol);
			secondType = ToFullyQualified(secondSymbol);
			return true;
		}

		return false;
	}

	private static bool TryGetSingleAttributeType(AttributeData attribute, out ITypeSymbol type)
	{
		type = null!;
		var attributeClass = attribute.AttributeClass;
		if (attributeClass is { TypeArguments.Length: 1 })
		{
			type = attributeClass.TypeArguments[0];
			return true;
		}

		if (attribute.ConstructorArguments.Length == 1 &&
			attribute.ConstructorArguments[0].Kind == TypedConstantKind.Type &&
			attribute.ConstructorArguments[0].Value is ITypeSymbol constructorType)
		{
			type = constructorType;
			return true;
		}

		return false;
	}

	private sealed class QueryModel(
		string      typeNamespace,
		string      typeName,
		string      hintName,
		string[]    all,
		string[]    none,
		string[]    any,
		string[]    optional,
		string[]    added,
		string[]    changed,
		string[]    unsupportedAttributeNames,
		Location    declarationLocation,
		bool        isPartial,
		bool        isReadOnly,
		bool        isNested,
		bool        isUnsupported,
		bool        isRecord,
		string[]    invalidComponents
	)
	{
		public bool IsNested   { get; } = isNested;
		public bool IsPartial  { get; } = isPartial;
		public bool IsReadOnly { get; } = isReadOnly;
		public bool IsUnsupported { get; } = isUnsupported;
		public bool IsRecord { get; } = isRecord;
		public string[] InvalidComponents { get; } = invalidComponents;

		public string[] All                { get; } = all;
		public string[] Added              { get; } = added;
		public string[] Any                { get; } = any;
		public string[] Changed            { get; } = changed;
		public string[] UnsupportedAttributeNames { get; } = unsupportedAttributeNames;
		public Location DeclarationLocation { get; } = declarationLocation;
		public string   HintName           { get; } = hintName;
		public string[] None               { get; } = none;
		public string[] Optional           { get; } = optional;
		public string   TypeName           { get; } = typeName;
		public string   Namespace          { get; } = typeNamespace;
	}
}
