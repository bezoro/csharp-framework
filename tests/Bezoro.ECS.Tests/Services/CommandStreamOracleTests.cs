using System;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class CommandStreamOracleTests
{
	[Fact]
	public void MixedBatches_MatchASequentialManagedReferenceModel()
	{
		const int entityCount = 17;
		using var world = new World(new WorldConfig
		{
			EntityCapacity = entityCount, QueryResultCapacity = entityCount, ComponentTypeCapacity = 4,
			CommandCapacity = 128, CommandPayloadCapacityPerType = 128, ChunkCapacity = 3
		});
		using var commands = world.CreateCommandStream();
		var entities = new Entity[entityCount];
		var positions = new int?[entityCount];
		var notes = new string?[entityCount];
		for (var index = 0; index < entityCount; index++) entities[index] = world.Spawn();
		var random = new Random(72831);
		for (var batch = 0; batch < 80; batch++)
		{
			for (var command = 0; command < 96; command++)
			{
				int index = random.Next(entityCount);
				switch (random.Next(4))
				{
					case 0:
						int value = random.Next();
						commands.Set(entities[index], new Position { Value = value });
						positions[index] = value;
						break;
					case 1:
						commands.Remove<Position>(entities[index]);
						positions[index] = null;
						break;
					case 2:
						string text = random.Next().ToString();
						commands.Set(entities[index], new Note { Text = text });
						notes[index] = text;
						break;
					default:
						commands.Remove<Note>(entities[index]);
						notes[index] = null;
						break;
				}
			}
			world.Playback(commands);
			for (var index = 0; index < entityCount; index++)
			{
				world.TryRead<Position>(entities[index], out var position).Should().Be(positions[index].HasValue);
				if (positions[index].HasValue) position.Value.Should().Be(positions[index]!.Value);
				world.TryRead<Note>(entities[index], out var note).Should().Be(notes[index] is not null);
				if (notes[index] is not null) note.Text.Should().Be(notes[index]);
			}
		}
	}

	private struct Position { public int Value; }
	private struct Note { public string Text; }
}
