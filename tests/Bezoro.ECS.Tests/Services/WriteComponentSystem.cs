using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Tests.Services;

internal sealed class WriteComponentSystem(Entity entity) : ISystem
{
	public void Update(in SystemContext context)
	{
		ref var position = ref context.World.Write<ErgonomicJobPosition>(entity);
		position.Value++;
	}
}
