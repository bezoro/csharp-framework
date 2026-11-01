using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Bezoro.ECS.SourceGen.Generators;

/// <summary>
///     Generates grouped ECS storage components and conversion helpers for split-field components.
/// </summary>
[Generator]
public sealed class SplitFieldSourceGenerator : IIncrementalGenerator
{
	private static readonly DiagnosticDescriptor UnsupportedDeclaration = new(
		"BECSG004", "Unsupported split-field declaration",
		"Split component '{0}' requires an accessible non-generic struct and accessible mutable non-pointer fields with non-negative group IDs",
		"Usage", DiagnosticSeverity.Error, true);
	private const string SPLIT_FIELDS_ATTRIBUTE_NAME = "Bezoro.ECS.Attributes.SplitFieldsAttribute";
	private const string SPLIT_GROUP_ATTRIBUTE_NAME  = "Bezoro.ECS.Attributes.SplitGroupAttribute";

	/// <inheritdoc />
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var models = context.CompilationProvider.Select(static (compilation, _) =>
			{
				var result = new List<SplitModel>();
				CollectSplitTypes(compilation.Assembly.GlobalNamespace, result);
				return result;
			}
		);

		context.RegisterSourceOutput(
			models, static (productionContext, splitModels) =>
			{
				for (var i = 0; i < splitModels.Count; i++)
				{
					var    model  = splitModels[i];
					if (model.IsUnsupported)
					{
						productionContext.ReportDiagnostic(Diagnostic.Create(UnsupportedDeclaration, model.Location, model.LogicalTypeName));
						continue;
					}
					string source = BuildSource(model);
					productionContext.AddSource(model.HintName, SourceText.From(source, Encoding.UTF8));
				}
			}
		);
	}

	private static bool HasAttribute(ISymbol symbol, string attributeTypeName)
	{
		var attributes = symbol.GetAttributes();
		for (var i = 0; i < attributes.Length; i++)
		{
			if (attributes[i].AttributeClass?.ToDisplayString() == attributeTypeName)
				return true;
		}

		return false;
	}

	private static bool TryCreateModel(INamedTypeSymbol type, out SplitModel model)
	{
		model = null!;
		string accessibility = ToAccessibility(type.DeclaredAccessibility);
		for (var owner = type.ContainingType; owner is not null; owner = owner.ContainingType)
			if (owner.DeclaredAccessibility != Accessibility.Public) accessibility = "internal";
		bool unsupported = !GeneratedNames.IsAccessible(type) || GeneratedNames.IsOpenType(type) || type.IsRefLikeType;

		var fieldsByGroup = new Dictionary<int, List<FieldModel>>();
		var members       = type.GetMembers();
		for (var i = 0; i < members.Length; i++)
		{
			if (members[i] is not IFieldSymbol field || field.IsStatic)
				continue;

			if (!TryGetGroupId(field, out int groupId))
				continue;
			if (groupId < 0 || field.IsReadOnly || field.IsFixedSizeBuffer || field.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer ||
				field.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
			{
				unsupported = true;
				continue;
			}

			if (!fieldsByGroup.TryGetValue(groupId, out var groupFields))
			{
				groupFields            = [];
				fieldsByGroup[groupId] = groupFields;
			}

			groupFields.Add(new(GeneratedNames.Identifier(field.Name), field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				ToAccessibility(field.DeclaredAccessibility)));
		}

		if (fieldsByGroup.Count == 0 && !unsupported)
			return false;

		var groups   = new List<GroupModel>(fieldsByGroup.Count);
		var groupIds = new List<int>(fieldsByGroup.Keys);
		groupIds.Sort();
		for (var i = 0; i < groupIds.Count; i++)
		{
			int groupId     = groupIds[i];
			var groupFields = fieldsByGroup[groupId];
			groupFields.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
			groups.Add(new(groupId, groupFields));
		}

		string namespaceName = type.ContainingNamespace.IsGlobalNamespace
								   ? string.Empty
								   : type.ContainingNamespace.ToDisplayString();

		string logicalTypeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		string helperTypeName  = GetHelperTypeName(type);
		string hintName = GeneratedNames.UniqueName(logicalTypeName) + ".SplitFields.g.cs";

		model = new(namespaceName, accessibility, helperTypeName, logicalTypeName, groups, hintName, unsupported,
			type.Locations.Length > 0 ? type.Locations[0] : Location.None);
		return true;
	}

	private static bool TryGetGroupId(IFieldSymbol field, out int groupId)
	{
		groupId = -1;
		for (var i = 0; i < field.GetAttributes().Length; i++)
		{
			var attribute = field.GetAttributes()[i];
			if (attribute.AttributeClass?.ToDisplayString() != SPLIT_GROUP_ATTRIBUTE_NAME)
				continue;

			if (attribute.ConstructorArguments.Length != 1)
				return false;

			var argument = attribute.ConstructorArguments[0];
			if (argument.Kind != TypedConstantKind.Primitive || argument.Value is not int value)
				return false;

			groupId = value;
			return true;
		}

		return false;
	}

	private static string BuildSource(SplitModel model)
	{
		var builder = new StringBuilder();
		builder.AppendLine("// <auto-generated />");

		if (!string.IsNullOrEmpty(model.Namespace))
		{
			builder.Append("namespace ").Append(model.Namespace).AppendLine(";");
			builder.AppendLine();
		}

		builder.Append(model.Accessibility).Append(" static class ").Append(model.HelperTypeName).AppendLine();
		builder.AppendLine("{");

		for (var groupIndex = 0; groupIndex < model.Groups.Count; groupIndex++)
		{
			var    group         = model.Groups[groupIndex];
			string groupTypeName = GetGroupTypeName(group.GroupId);
			builder.AppendLine("    /// <summary>");
			builder.Append("    /// Split group ").Append(group.GroupId).AppendLine(" storage component.");
			builder.AppendLine("    /// </summary>");
			builder.Append("    public struct ").Append(groupTypeName).AppendLine();

			builder.AppendLine("    {");
			for (var fieldIndex = 0; fieldIndex < group.Fields.Count; fieldIndex++)
			{
				var field = group.Fields[fieldIndex];
				builder.Append("        ").Append(field.Accessibility).Append(' ').Append(field.TypeName).Append(' ').Append(field.Name).AppendLine(";");
			}

			builder.AppendLine("    }");
			builder.AppendLine();
		}

		builder.Append(
			"    public static void Add(global::Bezoro.ECS.Abstractions.IWorld world, global::Bezoro.ECS.Types.Entity entity, in "
		).Append(model.LogicalTypeName).AppendLine(" component)");

		builder.AppendLine("    {");
		builder.AppendLine("        if (world is null) throw new global::System.ArgumentNullException(nameof(world));");
		for (var groupIndex = 0; groupIndex < model.Groups.Count; groupIndex++)
		{
			var group = model.Groups[groupIndex];
			builder.Append("        world.Add(entity, To").Append(GetGroupTypeName(group.GroupId))
				   .AppendLine("(component));");
		}

		builder.AppendLine("    }");
		builder.AppendLine();

		builder.Append(
			"    public static void Set(global::Bezoro.ECS.Abstractions.IWorld world, global::Bezoro.ECS.Types.Entity entity, in "
		).Append(model.LogicalTypeName).AppendLine(" component)");

		builder.AppendLine("    {");
		builder.AppendLine("        if (world is null) throw new global::System.ArgumentNullException(nameof(world));");
		for (var groupIndex = 0; groupIndex < model.Groups.Count; groupIndex++)
		{
			var group = model.Groups[groupIndex];
			builder.Append("        world.Set(entity, To").Append(GetGroupTypeName(group.GroupId))
				   .AppendLine("(component));");
		}

		builder.AppendLine("    }");
		builder.AppendLine();

		builder.AppendLine(
			"    public static void Remove(global::Bezoro.ECS.Abstractions.IWorld world, global::Bezoro.ECS.Types.Entity entity)"
		);

		builder.AppendLine("    {");
		builder.AppendLine("        if (world is null) throw new global::System.ArgumentNullException(nameof(world));");
		for (var groupIndex = 0; groupIndex < model.Groups.Count; groupIndex++)
		{
			var group = model.Groups[groupIndex];
			builder.Append("        world.Remove<").Append(GetGroupTypeName(group.GroupId)).AppendLine(">(entity);");
		}

		builder.AppendLine("    }");
		builder.AppendLine();

		builder.AppendLine(
			"    public static bool Has(global::Bezoro.ECS.Abstractions.IWorld world, global::Bezoro.ECS.Types.Entity entity)"
		);

		builder.AppendLine("    {");
		builder.AppendLine("        if (world is null) throw new global::System.ArgumentNullException(nameof(world));");
		if (model.Groups.Count == 0)
		{
			builder.AppendLine("        return false;");
		}
		else
		{
			builder.Append("        return ");
			for (var groupIndex = 0; groupIndex < model.Groups.Count; groupIndex++)
			{
				if (groupIndex > 0)
					builder.Append(" && ");

				builder.Append("world.Has<").Append(GetGroupTypeName(model.Groups[groupIndex].GroupId))
					   .Append(">(entity)");
			}

			builder.AppendLine(";");
		}

		builder.AppendLine("    }");
		builder.AppendLine();

		builder.Append(
			"    public static bool TryGet(global::Bezoro.ECS.Abstractions.IWorld world, global::Bezoro.ECS.Types.Entity entity, out "
		).Append(model.LogicalTypeName).AppendLine(" component)");

		builder.AppendLine("    {");
		builder.AppendLine("        if (world is null) throw new global::System.ArgumentNullException(nameof(world));");
		for (var groupIndex = 0; groupIndex < model.Groups.Count; groupIndex++)
		{
			var    group         = model.Groups[groupIndex];
			string groupTypeName = GetGroupTypeName(group.GroupId);
			string variableName  = "group" + group.GroupId;
			builder.Append("        if (!world.TryGet(entity, out ").Append(groupTypeName).Append(' ')
				   .Append(variableName).AppendLine("))");

			builder.AppendLine("        {");
			builder.AppendLine("            component = default;");
			builder.AppendLine("            return false;");
			builder.AppendLine("        }");
		}

		builder.AppendLine("        component = default;");
		for (var groupIndex = 0; groupIndex < model.Groups.Count; groupIndex++)
		{
			var    group        = model.Groups[groupIndex];
			string variableName = "group" + group.GroupId;
			for (var fieldIndex = 0; fieldIndex < group.Fields.Count; fieldIndex++)
			{
				var field = group.Fields[fieldIndex];
				builder.Append("        component.").Append(field.Name).Append(" = ").Append(variableName).Append('.')
					   .Append(field.Name).AppendLine(";");
			}
		}

		builder.AppendLine("        return true;");
		builder.AppendLine("    }");
		builder.AppendLine();

		for (var groupIndex = 0; groupIndex < model.Groups.Count; groupIndex++)
		{
			var    group         = model.Groups[groupIndex];
			string groupTypeName = GetGroupTypeName(group.GroupId);
			builder.Append("    private static ").Append(groupTypeName).Append(" To").Append(groupTypeName)
				   .Append("(in ").Append(model.LogicalTypeName).AppendLine(" component)");

			builder.AppendLine("    {");
			builder.Append("        return new ").Append(groupTypeName).AppendLine();
			builder.AppendLine("        {");
			for (var fieldIndex = 0; fieldIndex < group.Fields.Count; fieldIndex++)
			{
				var field = group.Fields[fieldIndex];
				builder.Append("            ").Append(field.Name).Append(" = component.").Append(field.Name)
					   .AppendLine(",");
			}

			builder.AppendLine("        };");
			builder.AppendLine("    }");
			builder.AppendLine();
		}

		builder.AppendLine("}");
		return builder.ToString();
	}

	private static string GetGroupTypeName(int groupId) => $"Group{groupId}";

	private static string GetHelperTypeName(INamedTypeSymbol type)
	{
		var names = new Stack<string>();
		for (var current = type.ContainingType; current is { }; current = current.ContainingType)
			names.Push(current.Name);

		var builder = new StringBuilder();
		while (names.Count > 0) builder.Append(names.Pop()).Append('.');

		builder.Append(type.Name);
		return GeneratedNames.UniqueName(builder.ToString()) + "SplitGenerated";
	}

	private static string ToAccessibility(Accessibility accessibility) =>
		accessibility switch
		{
			Accessibility.Public   => "public",
			Accessibility.Internal => "internal",
			_                      => string.Empty
		};

	private static void CollectSplitTypes(INamespaceSymbol namespaceSymbol, ICollection<SplitModel> output)
	{
		foreach (var member in namespaceSymbol.GetMembers())
		{
			if (member is INamespaceSymbol ns)
			{
				CollectSplitTypes(ns, output);
				continue;
			}

			if (member is INamedTypeSymbol type)
				CollectSplitTypes(type, output);
		}
	}

	private static void CollectSplitTypes(INamedTypeSymbol type, ICollection<SplitModel> output)
	{
		if (type.TypeKind == TypeKind.Struct &&
			HasAttribute(type, SPLIT_FIELDS_ATTRIBUTE_NAME) &&
			TryCreateModel(type, out var model))
			output.Add(model);

		var nestedTypes = type.GetTypeMembers();
		for (var i = 0; i < nestedTypes.Length; i++)
			CollectSplitTypes(nestedTypes[i], output);
	}

	private sealed class FieldModel(string name, string typeName, string accessibility)
	{
		public string Name     { get; } = name;
		public string TypeName { get; } = typeName;
		public string Accessibility { get; } = accessibility;
	}

	private sealed class GroupModel(int groupId, List<FieldModel> fields)
	{
		public int              GroupId { get; } = groupId;
		public List<FieldModel> Fields  { get; } = fields;
	}

	private sealed class SplitModel(
		string           typeNamespace,
		string           accessibility,
		string           helperTypeName,
		string           logicalTypeName,
		List<GroupModel> groups,
		string           hintName,
		bool             isUnsupported,
		Location         location
	)
	{
		public List<GroupModel> Groups          { get; } = groups;
		public string           Accessibility   { get; } = accessibility;
		public string           HelperTypeName  { get; } = helperTypeName;
		public string           HintName        { get; } = hintName;
		public string           LogicalTypeName { get; } = logicalTypeName;

		public string Namespace { get; } = typeNamespace;
		public bool IsUnsupported { get; } = isUnsupported;
		public Location Location { get; } = location;
	}
}
