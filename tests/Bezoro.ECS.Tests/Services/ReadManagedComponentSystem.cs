using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Tests.Services;

internal sealed class ReadManagedComponentSystem(Entity entity) : ISystem
{
	public void Update(in SystemContext context)
	{
		_ = context.World.TryRead(entity, out ErgonomicReadOnlyNote _);
	}
}
