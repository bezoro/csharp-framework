using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Tests.Services;

internal sealed class WriteResourceSystem : ISystem
{
	public void Update(in SystemContext context)
	{
		ref var resource = ref context.World.WriteResource<ErgonomicSchedulerResource>();
	}
}
