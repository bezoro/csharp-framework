using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

[TestSubject(typeof(World))]
public class SplitFieldGeneratorTests
{
	[Fact]
	public void SplitGeneratedGroups_WhenQueriedByHotGroup_ShouldIterateWithoutColdGroup()
	{
		using var world  = new World();
		var       first  = world.Spawn();
		var       second = world.Spawn();

		SplitTransformSplitGenerated.Add(
			world, first, new() { PositionX = 1f, PositionY = 2f, RotationZ = 3f, Scale = 4f }
		);

		SplitTransformSplitGenerated.Add(
			world, second, new() { PositionX = 5f, PositionY = 6f, RotationZ = 7f, Scale = 8f }
		);

		var handle = world.Compile<SplitGroup0QuerySpec>();
		var sum    = 0f;
		using (var cursor = world.Execute(handle))
		{
			cursor.MoveNext().Should().BeTrue();
			for (var i = 0; i < cursor.Current.Length; i++)
			{
				ref var group = ref cursor.Write<SplitTransformSplitGenerated.Group0>(i);
				sum += group.PositionX + group.PositionY + group.RotationZ;
			}
		}

		sum.Should().Be(24f);
	}

	[Fact]
	public void SplitGeneratedHelpers_WhenAddingSplitComponent_ShouldStoreAndRehydrateGroups()
	{
		using var world  = new World();
		var       entity = world.Spawn();
		var input = new SplitTransform
		{
			PositionX = 3f,
			PositionY = 4f,
			RotationZ = 90f,
			Scale     = 2f
		};

		SplitTransformSplitGenerated.Add(world, entity, in input);

		world.Has<SplitTransformSplitGenerated.Group0>(entity).Should().BeTrue();
		world.Has<SplitTransformSplitGenerated.Group1>(entity).Should().BeTrue();
		SplitTransformSplitGenerated.TryGet(world, entity, out var restored).Should().BeTrue();
		restored.Should().BeEquivalentTo(input);
	}

	private readonly struct SplitGroup0QuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.All<SplitTransformSplitGenerated.Group0>();
	}
}

[SplitFields]
internal struct SplitTransform
{
	[SplitGroup(0)] public float PositionX;
	[SplitGroup(0)] public float PositionY;
	[SplitGroup(0)] public float RotationZ;
	[SplitGroup(1)] public float Scale;
}
