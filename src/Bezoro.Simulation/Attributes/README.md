# Attributes and modifiers

`Bezoro.Simulation.Attributes` evaluates game-defined numeric attributes such as movement speed, attack damage, resistance, and maximum health. It has no engine, event-bus, serialization, or wall-clock dependency.

```csharp
using Bezoro.Simulation.Attributes;

var damage = new StatId(1);
var speed = new StatId(2);
var attributes = new StatSet(new[]
{
    new StatDefinition(damage, baseValue: 100, minimum: 0, maximum: 1000),
    new StatDefinition(speed, baseValue: 5, minimum: 0, maximum: 30)
}, modifierCapacity: 32);

var equipment = attributes.AddModifier(damage,
    new StatModifier(StatModifierOperation.Add, 20));
var buff = attributes.AddModifier(damage,
    new StatModifier(StatModifierOperation.AddPercent, 0.25));

double currentDamage = attributes.GetValue(damage); // 150
attributes.TryRemoveModifier(buff);
currentDamage = attributes.GetValue(damage); // 120
attributes.SetBaseValue(damage, 110);
attributes.TryUpdateModifier(equipment,
    new StatModifier(StatModifierOperation.Add, 30));
currentDamage = attributes.GetValue(damage); // 140
```

Keep identifier assignments stable in application save formats. Zero is invalid; an identifier must be constructed from a positive integer. `Contains` returns false for default or unknown identifiers; value and definition lookups reject them. A set copies its definitions and has a fixed number of attributes. `SetDefinition` replaces the base value and bounds of an existing identifier; it does not add an attribute or rename one.

`StatModifier` stores its double value and two integer fields in 16 bytes, avoiding padding in modifier arrays. Handles are also 16-byte values. Referenced application objects and array headers are additional storage.

## Formula and ordering

For each attribute:

```text
value = (base + sum(Add)) * (1 + sum(AddPercent)) * product(Multiply)
```

`AddPercent` takes fractional percentages: `0.25` means an additive 25 percent increase; `-0.25` means a 25 percent decrease. `Multiply` takes a direct, nonnegative factor: `1.25` multiplies by 1.25 and `0` multiplies by zero. The two operations intentionally express different stacking rules. Combined percentages may fall below `-1`; use a zero minimum when negative output is not allowed.

If any `Override` modifiers exist, the one with highest `Priority` replaces the formula result. Equal-priority overrides use the latest inserted live modifier. Updating a modifier preserves its original position. Removing another modifier or reusing its storage slot does not change insertion order. Priority is ignored for operations other than overrides. The definition's inclusive minimum and maximum apply after the formula or override.

The pure `StatCalculator.Evaluate(definition, ReadOnlySpan<StatModifier>)` uses the same formula and treats span order as insertion order. It does not modify the input or allocate temporary collections:

```csharp
Span<StatModifier> modifiers = stackalloc StatModifier[2];
modifiers[0] = new StatModifier(StatModifierOperation.Add, 20);
modifiers[1] = new StatModifier(StatModifierOperation.AddPercent, 0.25);
var evaluated = StatCalculator.Evaluate(
    new StatDefinition(damage, 100, 0, 1000), modifiers);
```

`StatEvaluation.Value` is the bounded result. `UnclampedValue` is the finite value before the definition's bounds, and `WasClamped` reports whether those bounds changed it.

## Numeric contract

Definitions and modifier constructors reject NaN and infinity. Minimum must not exceed maximum; the base value may lie outside the output bounds. All public value models are readonly structs. Their default values are intentional: `default(StatId)` and `default(StatDefinition)` are uninitialized and rejected when a definition or identifier is required; `default(StatModifier)` is additive zero.

Undefined modifier operations, including negative enum values, throw `ArgumentOutOfRangeException` under both normal and checked integer compilation.

Each overflowing addition or multiplication saturates to `double.MinValue` or `double.MaxValue`. Category sums and products fold in insertion order, followed by the formula above. This keeps intermediate values finite, including an overflowing product followed by a zero factor. `WasSaturated` reports any overflow in this non-overridden computation, even when a later zero factor makes the final result zero. When an override wins, ignored arithmetic does not set this flag.

Saturation is not arbitrary-precision arithmetic. For example, the additions `MaxValue`, `MaxValue`, and `MinValue` fold to zero after the first overflowing step saturates. Normal double rounding and saturation follow input order; the library does not promise bit-identical floating-point results across all CPUs, compilers, or game runtimes. Use fixed-point application data if that guarantee is required by a network protocol.

## Capacity, lifetime, and complexity

`StatSet` allocates definition, cache, indexing, and modifier storage in its constructor. `TryAddModifier` returns false when no reusable slot remains; `AddModifier` throws. Invalid identifiers and unknown attributes throw without changing existing state. Getters and valid mutations do not allocate after construction. There are no per-frame tasks, callbacks, event allocations, or temporary modifier arrays.

| Operation | Expected cost |
| --- | --- |
| Cached value/result read | O(1) |
| Add, update, or remove a modifier | O(1) |
| Evaluate a changed attribute with M modifiers | O(M) |
| Copy or clear one attribute's M modifiers | O(M) |
| Clear all attributes | O(S + M) |
| Retained storage for S attributes and capacity C | O(S + C) |

Only a modified attribute's cache is invalidated. Modifiers are linked in insertion order inside the preallocated pool. A removed slot can be reused, but its generation changes. Default, foreign-set, removed, and cleared handles return false from the `Try*Modifier` methods. A slot at its final `uint` generation is permanently retired instead of wrapping; `AvailableModifierSlots` accounts for those retired slots. Owner identities also stop at exhaustion instead of wrapping.

Handles identify a live modifier inside one set. They are not save-game or cross-session identifiers. Persist definitions and ordered modifier values instead. `GetModifierCount` and `CopyModifiersTo` support copying into application-owned buffers. An undersized destination is rejected before any writes. `ClearModifiers` invalidates handles while retaining storage for reuse; there are no unmanaged resources requiring disposal.

## Unity, Godot, and ECS ownership

Own a `StatSet` from a Unity component, Godot node, or ECS resource. All reads that can refresh caches and all mutations require one owning thread or external serialization. Separate sets can run on separate workers. A pure calculator call can run concurrently when its input spans are not mutated by another caller. Scene, UI, and rendering calls remain the host engine's responsibility.

For a Bezoro ECS world, the same set can be installed as a resource:

```csharp
using Bezoro.ECS.Services;

using var world = new World();
world.SetResource(attributes);
double attack = world.ReadResource<StatSet>().GetValue(damage);
```

Application code decides when to add or remove buffs. A tick scheduler can carry `StatModifierHandle` values as expiry data; when an expiry event is drained, call `TryRemoveModifier`. Removing an effect early or clearing the set makes a later expiry harmless. The attribute module does not create its own timer loop or retain engine objects.

Both .NET 10 and .NET Standard 2.1 targets expose the same API. Unity 6 imports the precompiled .NET Standard DLL. Validate numeric behavior and frame budgets in the actual Mono/IL2CPP or Godot export configuration.
