using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Immutable;
using Bezoro.ECS.Services;
using Bezoro.ECS.SourceGen.Generators;
using Bezoro.ECS.Types;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class SourceGeneratorCompilationTests
{
	[Fact]
	public void GeneratedQueries_CompileGenericRecordAndEscapedIdentifiers()
	{
		Compile("""
			using Bezoro.ECS.Attributes;
			[Query, With(typeof(int))]
			public readonly partial record struct @class<T> where T : class, new();
			[Query, With(typeof(int))]
			public partial struct @event<T> where T : unmanaged;
			""").GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
	}

	[Fact]
	public void GeneratedJobExtensions_CompileGenericAndNestedGenericJobsWithConstraints()
	{
		Compile("""
			using Bezoro.ECS.Abstractions;
			using Bezoro.ECS.Services;
			using Bezoro.ECS.Types;
			public struct Component<T> where T : unmanaged { public T Value; }
			public struct Job<TSpec> : IForEach<Component<TSpec>> where TSpec : unmanaged
			{
			    public void Execute(ref Component<TSpec> component) { }
			}
			public class Container<T> where T : unmanaged
			{
			    public struct Job<U> : IForEach<Component<T>> where U : class, new()
			    {
			        public void Execute(ref Component<T> component) { }
			    }
			}
			public readonly struct Query : ICompiledQuerySpec
			{
			    public void Build(ref QueryBuilder builder) => builder.With<Component<int>>();
			}
			public static class Usage
			{
			    public static void Run(World world)
			    {
			        world.Query<Query>().Run(new Job<int>());
			        world.Query<Query>().RunParallel(new Container<int>.Job<object>(), 2);
			    }
			}
			""").GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
	}

	[Fact]
	public void GeneratedJobExtensions_PreserveNullableReferenceConstraintsWithoutWarnings()
	{
		var compilation = Compile("""
			#nullable enable
			using Bezoro.ECS.Abstractions;
			using Bezoro.ECS.Services;
			using Bezoro.ECS.Types;
			public struct Job<T> : IForEach<int> where T : class?
			{
			    public void Execute(ref int component) { }
			}
			public readonly struct Query : ICompiledQuerySpec { public void Build(ref QueryBuilder builder) => builder.With<int>(); }
			public static class Usage { public static void Run(World world) => world.Query<Query>().Run(new Job<string?>()); }
			""");
		compilation.GetDiagnostics().Should().NotContain(diagnostic => diagnostic.Id == "CS8634" || diagnostic.Id == "CS8669");
		compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
	}

	[Fact]
	public void GeneratedNames_DoNotCollideForNamespaceAndUnderscoreBoundaries()
	{
		Compile("""
			using Bezoro.ECS.Abstractions;
			using Bezoro.ECS.Attributes;
			namespace A_B
			{
			    [Query] public partial struct C;
			    public struct Job : IForEach<int> { public void Execute(ref int component) { } }
			}
			namespace A
			{
			    [Query] public partial struct B_C;
			    public struct B_Job : IForEach<int> { public void Execute(ref int component) { } }
			}
			""").GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
	}

	[Theory]
	[InlineData("[Query] public struct Q;", "BECSG002")]
	[InlineData("public class C { [Query] public partial struct Q; }", "BECSG002")]
	[InlineData("[Query, With(typeof(string))] public partial struct Q;", "BECSG003")]
	[InlineData("[Query, With(typeof(int?))] public partial struct Q;", "BECSG003")]
	[InlineData("[SplitFields] public struct C { [SplitGroup(0)] public readonly int Value; }", "BECSG004")]
	[InlineData("[SplitFields] public struct C<T> { [SplitGroup(0)] public T Value; }", "BECSG004")]
	public void UnsupportedDeclarations_ReportActionableDiagnostics(string declaration, string expectedId)
	{
		Compile("using Bezoro.ECS.Attributes; " + declaration, expectedId);
	}

	[Fact]
	public void SplitFields_EscapesKeywordFieldNames()
	{
		Compile("""
			using Bezoro.ECS.Attributes;
			[SplitFields] public struct Component { [SplitGroup(0)] public int @event; }
			""").GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
	}

	[Fact]
	public void SplitFields_PreservesEffectiveAccessibilityAndDistinctNestedNames()
	{
		Compile("""
			using Bezoro.ECS.Attributes;
			public class Outer { [SplitFields] public struct Inner { [SplitGroup(0)] public int Value; } }
			[SplitFields] public struct Outer_Inner { [SplitGroup(0)] public int Value; }
			internal class InternalOuter { [SplitFields] public struct Inner { [SplitGroup(0)] public int Value; } }
			internal struct Hidden;
			[SplitFields] public struct Component { [SplitGroup(0)] internal Hidden Value; }
			""").GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
	}

	[Fact]
	public void Metadata_InfersReadOnlyAndExplicitEntityJobCallsAndSkipsOpenSystemTypes()
	{
		var compilation = Compile("""
			using Bezoro.ECS.Abstractions;
			using Bezoro.ECS.Types;
			public readonly struct Query : ICompiledQuerySpec
			{
			    public void Build(ref QueryBuilder builder) => builder.With<int>();
			}
			public struct Job : IForEachEntity<int>
			{
			    public void Execute(Entity entity, ref int component) { }
			}
			public sealed class ReadSystem : ISystem
			{
			    public void Update(in SystemContext context) => context.World.Query<Query>().ForEachRead<int>((Entity e, in int value) => { });
			}
			public sealed class WriteSystem : ISystem
			{
			    public void Update(in SystemContext context) => context.World.Query<Query>().RunEntity<Job, int>(new Job());
			}
			public class GenericSystem<T> : ISystem { public void Update(in SystemContext context) { } }
			""");
		using var binary = new MemoryStream();
		var emitted = compilation.Emit(binary);
		emitted.Success.Should().BeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
		var assembly = Assembly.Load(binary.ToArray());
		var metadata = (SystemMetadata[])assembly.GetType("Bezoro.ECS.Generated.GeneratedSystemMetadata")!
			.GetProperty("All", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
		metadata.Single(item => item.SystemType.Name == "ReadSystem").Reads.Should().Contain(typeof(int));
		metadata.Single(item => item.SystemType.Name == "ReadSystem").Writes.Should().BeEmpty();
		metadata.Single(item => item.SystemType.Name == "WriteSystem").Writes.Should().Contain(typeof(int));
	}

	[Fact]
	public void Metadata_UsesEffectiveInheritedGenericAndExplicitUpdateImplementations()
	{
		var metadata = EmitMetadata(Compile("""
			using Bezoro.ECS.Abstractions;
			using Bezoro.ECS.Types;
			public struct Component<T> { public T Value; }
			public class WriterBase<T> : ISystem
			{
			    public virtual void Update(in SystemContext context) => context.World.Write<Component<T>>(new Entity()).Value = default;
			}
			public sealed class WriterDerived : WriterBase<int> { }
			public sealed class EmptyOverride : WriterBase<int> { public override void Update(in SystemContext context) { } }
			public struct Query : ICompiledQuerySpec { public void Build(ref QueryBuilder builder) => builder.With<int>(); }
			public sealed class ExplicitWriter : ISystem
			{
			    void ISystem.Update(in SystemContext context)
			    {
			        using var cursor = context.World.Execute(context.World.Compile<Query>());
			        cursor.MoveNext();
			        cursor.Write<int>(0)++;
			    }
			}
			public sealed class CursorReader : ISystem
			{
			    public void Update(in SystemContext context)
			    {
			        using var cursor = context.World.Execute(context.World.Compile<Query>());
			        cursor.MoveNext();
			        _ = cursor.Read<int>(0);
			    }
			}
			public sealed class AccessorWriter : ISystem
			{
			    public void Update(in SystemContext context) => context.World.GetAccessor<int>().Write(new Entity())++;
			}
			"""));
		metadata.Single(item => item.SystemType.Name == "WriterDerived").Writes.Should()
			.ContainSingle(type => type.IsGenericType && type.GenericTypeArguments.Single() == typeof(int));
		metadata.Single(item => item.SystemType.Name == "EmptyOverride").Writes.Should().BeEmpty();
		metadata.Single(item => item.SystemType.Name == "ExplicitWriter").Writes.Should().Contain(typeof(int));
		metadata.Single(item => item.SystemType.Name == "CursorReader").Reads.Should().Contain(typeof(int));
		metadata.Single(item => item.SystemType.Name == "AccessorWriter").Writes.Should().Contain(typeof(int));
	}

	[Fact]
	public void Metadata_UnknownInheritedBodyFromAnotherAssemblyIsExclusive()
	{
		var externalBase = Compile("""
			using Bezoro.ECS.Abstractions;
			using Bezoro.ECS.Types;
			public class ExternalBase : ISystem
			{
			    public void Update(in SystemContext context) => context.World.Write<int>(new Entity())++;
			}
			""");
		using var binary = new MemoryStream();
		var emitted = externalBase.Emit(binary);
		emitted.Success.Should().BeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
		binary.Position = 0;
		AssemblyLoadContext.Default.LoadFromStream(binary);
		var metadata = EmitMetadata(Compile("public sealed class Derived : ExternalBase { }", additionalReference:
			MetadataReference.CreateFromImage(binary.ToArray())));
		metadata.Single(item => item.SystemType.Name == "Derived").IsExclusive.Should().BeTrue();
	}

	[Fact]
	public void Metadata_OpaqueWorldEscapeRemainsExclusiveAlongsideKnownAccess()
	{
		var metadata = EmitMetadata(Compile("""
			using Bezoro.ECS.Abstractions;
			using Bezoro.ECS.Services;
			using Bezoro.ECS.Types;
			using Bezoro.ECS.Attributes;
			public sealed class MixedSystem : ISystem
			{
			    public void Update(in SystemContext context)
			    {
			        context.World.Write<float>(new Entity())++;
			        Helper(context.World);
			    }
			    private static void Helper(World world) => world.Write<int>(new Entity())++;
			}
			[Writes(typeof(int))]
			public sealed class StructuralSystem : ISystem
			{
			    public void Update(in SystemContext context) => context.World.Add(new Entity(), 42);
			}
			[Writes(typeof(int))]
			public sealed class ResetSystem : ISystem
			{
			    public void Update(in SystemContext context) => context.World.Reset();
			}
			[Writes(typeof(int))]
			public sealed class RegistrationSystem : ISystem
			{
			    public void Update(in SystemContext context) => context.World.AddSystem(new ResetSystem());
			}
			[Writes(typeof(int))]
			public sealed class SetConfigurationSystem : ISystem
			{
			    public void Update(in SystemContext context) => context.World.SetSystemSetEnabled<int>(false);
			}
			public sealed class ConstructorEscapeSystem : ISystem
			{
			    public void Update(in SystemContext context)
			    {
			        context.World.Write<float>(new Entity())++;
			        new Helper(context.World).Apply();
			    }
			}
			public sealed class Helper(World world)
			{
			    public void Apply() => world.Write<int>(new Entity())++;
			}
			"""));
		metadata.Single(item => item.SystemType.Name == "MixedSystem").IsExclusive.Should().BeTrue();
		metadata.Single(item => item.SystemType.Name == "StructuralSystem").IsExclusive.Should().BeTrue();
		metadata.Single(item => item.SystemType.Name == "ResetSystem").IsExclusive.Should().BeTrue();
		metadata.Single(item => item.SystemType.Name == "RegistrationSystem").IsExclusive.Should().BeTrue();
		metadata.Single(item => item.SystemType.Name == "SetConfigurationSystem").IsExclusive.Should().BeTrue();
		metadata.Single(item => item.SystemType.Name == "ConstructorEscapeSystem").IsExclusive.Should().BeTrue();
	}

	private static SystemMetadata[] EmitMetadata(Compilation compilation)
	{
		using var binary = new MemoryStream();
		var emitted = compilation.Emit(binary);
		emitted.Success.Should().BeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
		return (SystemMetadata[])Assembly.Load(binary.ToArray()).GetType("Bezoro.ECS.Generated.GeneratedSystemMetadata")!
			.GetProperty("All", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
	}

	private static Compilation Compile(string source, string? expectedDiagnostic = null, MetadataReference? additionalReference = null)
	{
		var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
		var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
			.Append(typeof(World).Assembly.Location).Append(typeof(Bezoro.Core.Utilities.Constants).Assembly.Location)
			.Distinct().Select(static path => MetadataReference.CreateFromFile(path));
		var compilation = CSharpCompilation.Create("GeneratorProbe_" + Guid.NewGuid().ToString("N"),
			[CSharpSyntaxTree.ParseText(source, parseOptions)], additionalReference is null ? references : references.Append(additionalReference),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		GeneratorDriver driver = CSharpGeneratorDriver.Create(
			ImmutableArray.Create<ISourceGenerator>(new QuerySourceGenerator().AsSourceGenerator(),
				new ForEachJobSourceGenerator().AsSourceGenerator(), new SplitFieldSourceGenerator().AsSourceGenerator(),
				new SystemMetadataGenerator().AsSourceGenerator()), parseOptions: parseOptions);
		driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
		if (expectedDiagnostic is null) diagnostics.Should().BeEmpty();
		else diagnostics.Should().ContainSingle(diagnostic => diagnostic.Id == expectedDiagnostic);
		return output;
	}
}
