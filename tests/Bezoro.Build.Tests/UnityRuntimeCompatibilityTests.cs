using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Core.Types;
using Bezoro.ECS.Services;
using Bezoro.GameSystems.HealthSystem.Types;
using Bezoro.Logging.Types;
using Bezoro.TypingSystem.Types;
using Bezoro.UnityApiSmoke;
using FluentAssertions;
using Xunit;

namespace Bezoro.Build.Tests;

public sealed class UnityRuntimeCompatibilityTests
{
    [Fact]
    public async Task StandardConsumers_CanCallInitializersOnModernRuntimeAssemblies()
    {
        // Real .NET Standard IL calls setters on the .NET runtime builds, including init modifiers.
        // A worker keeps this engine-free synchronous smoke independent of xUnit's async context.
        await Task.Run(() =>
        {
            FoundationConsumer.ExerciseDataApis().Should().Be(4);
            ToolkitConsumer.ExerciseDataApis().Should().Be(4);
            BehaviorConsumer.ExerciseDataApis().Should().Be(2);
            FrameworkConsumer.ExerciseDataApis().Should().Be(97);
        });
    }

    [Fact]
    public void ModernAssemblies_ForwardTheStandardInitModifierToTheRuntimeType()
    {
        Assembly[] assemblies =
        [
            typeof(PoolOptions).Assembly, typeof(World).Assembly, typeof(Health).Assembly,
            typeof(LogPayload).Assembly, typeof(TypingState).Assembly,
            typeof(UciPlayableMatchSession).Assembly, typeof(UciGameEngineSession).Assembly
        ];
        foreach (var assembly in assemblies)
        {
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName.Should().Be(".NETCoreApp,Version=v10.0");
            assembly.GetForwardedTypes().Should().Contain(typeof(IsExternalInit));
        }
    }
}
