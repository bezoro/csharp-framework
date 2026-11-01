using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class SystemRegistrationInspector(GeneratedSystemMetadataResolver metadataResolver)
{
	private readonly GeneratedSystemMetadataResolver _metadataResolver = metadataResolver;

	public SystemState Inspect(
		World             world,
		ISystem           system,
		Stage?            explicitStage,
		Func<Type, int>   resourceAccessIdResolver,
		Action<Type>      ensureSystemSetKnown)
	{
		var readSet                   = new HashSet<int>();
		var writeSet                  = new HashSet<int>();
		var systemSetTypes            = new HashSet<Type>();
		List<ISystemRunCondition>? runConditions = null;
		var type                      = system.GetType();
		var isExclusive               = false;
		var hasNoWorldAccessDeclaration = false;

		if (_metadataResolver.TryGet(type, out var metadata))
		{

			for (var i = 0; i < metadata.Reads.Length; i++)
			{
				var componentType = metadata.Reads[i];
				if (componentType is not null)
					AddReadType(world, readSet, writeSet, componentType);
			}

			for (var i = 0; i < metadata.Writes.Length; i++)
			{
				var componentType = metadata.Writes[i];
				if (componentType is not null)
					AddWriteType(world, readSet, writeSet, componentType);
			}

			for (var i = 0; i < metadata.ReadResources.Length; i++)
			{
				var resourceType = metadata.ReadResources[i];
				if (resourceType is not null)
					AddReadResourceType(readSet, writeSet, resourceType, resourceAccessIdResolver);
			}

			for (var i = 0; i < metadata.WriteResources.Length; i++)
			{
				var resourceType = metadata.WriteResources[i];
				if (resourceType is not null)
					AddWriteResourceType(readSet, writeSet, resourceType, resourceAccessIdResolver);
			}

			isExclusive = metadata.IsExclusive;
		}

		var hasAttributeAccessMetadata = false;
		foreach (object? attribute in type.GetCustomAttributes(true))
		{
			if (attribute is null)
				continue;

			switch (attribute)
			{
				case NoWorldAccessAttribute:
					hasNoWorldAccessDeclaration = true;
					continue;
				case ExclusiveAttribute:
					isExclusive = true;
					continue;
				case ReadsAttribute readsAttribute:
					AddReadType(world, readSet, writeSet, readsAttribute.ComponentType);
					hasAttributeAccessMetadata = true;
					continue;
				case WritesAttribute writesAttribute:
					AddWriteType(world, readSet, writeSet, writesAttribute.ComponentType);
					hasAttributeAccessMetadata = true;
					continue;
				case ReadsResourceAttribute readsResourceAttribute:
					AddReadResourceType(
						readSet,
						writeSet,
						readsResourceAttribute.ResourceType,
						resourceAccessIdResolver
					);
					hasAttributeAccessMetadata = true;
					continue;
				case WritesResourceAttribute writesResourceAttribute:
					AddWriteResourceType(
						readSet,
						writeSet,
						writesResourceAttribute.ResourceType,
						resourceAccessIdResolver
					);
					hasAttributeAccessMetadata = true;
					continue;
				case SystemSetAttribute systemSetAttribute:
				{
					var setType = systemSetAttribute.SetType;
					systemSetTypes.Add(setType);
					ensureSystemSetKnown(setType);
					continue;
				}
				case RunIfAttribute runIfAttribute:
					(runConditions ??= []).Add(CreateRunCondition(runIfAttribute.RunConditionType));
					continue;
			}
		}

		if (!hasAttributeAccessMetadata && !hasNoWorldAccessDeclaration)
			isExclusive = true;

		var beforeSystemTypes = new HashSet<Type>();
		var afterSystemTypes  = new HashSet<Type>();
		CollectOrderingConstraints(type, beforeSystemTypes, afterSystemTypes);
		return new(
			system,
			explicitStage ?? system.Stage,
			ToArray(readSet),
			ToArray(writeSet),
			isExclusive,
			ToOrderedTypeArray(beforeSystemTypes),
			ToOrderedTypeArray(afterSystemTypes),
			ToOrderedTypeArray(systemSetTypes),
			runConditions?.ToArray() ?? []
		);
	}

	private static Type[] ToOrderedTypeArray(HashSet<Type> types) =>
		types.OrderBy(static type => type.FullName, StringComparer.Ordinal)
			 .ToArray();

	private static int[] ToArray(HashSet<int> set)
	{
		var array = new int[set.Count];
		var index = 0;
		foreach (int value in set)
			array[index++] = value;

		Array.Sort(array);
		return array;
	}

	private static void AddReadType(World world, HashSet<int> readSet, HashSet<int> writeSet, Type componentType)
	{
		int typeId = world.GetOrCreateComponentTypeId(componentType);
		if (!writeSet.Contains(typeId))
			readSet.Add(typeId);
	}

	private static void AddReadResourceType(
		HashSet<int>    readSet,
		HashSet<int>    writeSet,
		Type            resourceType,
		Func<Type, int> resourceAccessIdResolver)
	{
		int accessId = resourceAccessIdResolver(resourceType);
		if (!writeSet.Contains(accessId))
			readSet.Add(accessId);
	}

	private static void AddWriteType(World world, HashSet<int> readSet, HashSet<int> writeSet, Type componentType)
	{
		int typeId = world.GetOrCreateComponentTypeId(componentType);
		writeSet.Add(typeId);
		readSet.Remove(typeId);
	}

	private static void AddWriteResourceType(
		HashSet<int>    readSet,
		HashSet<int>    writeSet,
		Type            resourceType,
		Func<Type, int> resourceAccessIdResolver)
	{
		int accessId = resourceAccessIdResolver(resourceType);
		writeSet.Add(accessId);
		readSet.Remove(accessId);
	}

	private static void CollectOrderingConstraints(Type type, HashSet<Type> beforeSet, HashSet<Type> afterSet)
	{
		foreach (object? attribute in type.GetCustomAttributes(true))
		{
			if (attribute is null)
				continue;

			switch (attribute)
			{
				case BeforeAttribute beforeAttribute:
					beforeSet.Add(beforeAttribute.SystemType);
					break;
				case AfterAttribute afterAttribute:
					afterSet.Add(afterAttribute.SystemType);
					break;
			}
		}
	}

	private static ISystemRunCondition CreateRunCondition(Type runConditionType)
	{
		if (!typeof(ISystemRunCondition).IsAssignableFrom(runConditionType))
			throw new InvalidOperationException(
				$"Run condition type '{runConditionType.FullName}' must implement '{typeof(ISystemRunCondition).FullName}'."
			);

		try
		{
			return (ISystemRunCondition)(Activator.CreateInstance(runConditionType) ??
										 throw new InvalidOperationException(
											 $"Run condition type '{runConditionType.FullName}' could not be created."
										 ));
		}
		catch (MissingMethodException ex)
		{
			throw new InvalidOperationException(
				$"Run condition type '{runConditionType.FullName}' must declare a public parameterless constructor.",
				ex
			);
		}
	}
}
