# Bezoro.Conditions

Typed Boolean conditions for transition guards, behavior-tree decisions and application rules. Definitions compile once into immutable instructions; each evaluation uses caller-owned scratch. Runtime targets are .NET 10 and .NET Standard 2.1, with no engine or package dependency.

## Define a rule

```csharp
using System;
using Bezoro.Conditions;

var rule = ConditionExpression.All(
    ConditionExpression.Predicate((int)NpcPredicate.HasTarget),
    ConditionExpression.Not(ConditionExpression.Predicate((int)NpcPredicate.IsStunned)),
    ConditionExpression.Any(
        ConditionExpression.Predicate((int)NpcPredicate.CanAttack),
        ConditionExpression.Predicate((int)NpcPredicate.CanApproach)))
    .Compile(maximumNodes: 64, maximumInstructions: 128);

var context = new NpcContext(hasTarget: true, stunned: false, distance: 6, stamina: 10);
var evaluator = new NpcPredicates();
// Allocate once per execution owner; reuse for subsequent evaluations.
var scratch = new bool[rule.RequiredStackSize];
bool canAct = rule.Evaluate(in context, ref evaluator, scratch);

readonly struct NpcContext
{
    public NpcContext(bool hasTarget, bool stunned, float distance, int stamina)
    {
        HasTarget = hasTarget;
        Stunned = stunned;
        Distance = distance;
        Stamina = stamina;
    }
    public bool HasTarget { get; }
    public bool Stunned { get; }
    public float Distance { get; }
    public int Stamina { get; }
}

enum NpcPredicate { HasTarget, IsStunned, CanAttack, CanApproach }

struct NpcPredicates : IConditionEvaluator<NpcContext>
{
    public bool Evaluate(int predicateId, in NpcContext context) => (NpcPredicate)predicateId switch
    {
        NpcPredicate.HasTarget => context.HasTarget,
        NpcPredicate.IsStunned => context.Stunned,
        NpcPredicate.CanAttack => context.Distance <= 2 && context.Stamina >= 5,
        NpcPredicate.CanApproach => context.Distance > 2 && context.Distance < 20,
        _ => throw new ArgumentOutOfRangeException(nameof(predicateId))
    };
}
```

Predicate identifiers are nonnegative application-defined integers. The evaluator determines their meaning and may use ordinary C# comparisons, arithmetic, method calls and `if` expressions. There is no expression-language parser, reflection lookup, string-key access or universal boxed value container. Keep context access consistent while evaluating one rule. Applications own numeric policies, predicate exceptions and any external work their predicates initiate.

The enum supplies application names while compiled rules store integer identifiers. The library does not inspect enums or context types through reflection.

## Boolean operations

| Expression | Evaluation |
| --- | --- |
| `True`, `False` | Constant result with no predicate calls. |
| `Predicate(id)` | Calls the evaluator for that ID. |
| `All(children)` or `left.And(right)` | Left to right; stops at the first false result. Empty All is true. |
| `Any(children)` or `left.Or(right)` | Left to right; stops at the first true result. Empty Any is false. |
| `Not(child)` | Negates the child's result. |
| `Xor(left, right)` | Evaluates both sides, left first, then tests inequality. |
| `Equal(left, right)` | Evaluates both sides, left first, then tests equality. |
| `Choose(test, whenTrue, whenFalse)` | Evaluates the test and only the selected branch, like `test ? a : b`. |

`All` and `Any` accept arrays or spans and own their retained children. Factories remove literal identities, prune after literal short-circuit results, fold literal Not/Xor/Equal operations, and select a literal Choose branch. They validate all supplied children first and preserve every predicate call that can occur before a short circuit. Statically discarded branches reserve no program instructions or scratch. Expressions are immutable. A shared expression object can appear multiple times in another expression; each occurrence retains its own evaluation position. The program does not cache repeated predicate IDs or carry values between calls. This preserves the order and short-circuit behavior of the equivalent C# Boolean expression.

Predicates should observe context rather than change it. Bookkeeping such as recording visited predicates is allowed, but an application that mutates its context during evaluation is responsible for those semantics. An exception propagates; it does not become false. The same program can be evaluated again after a failure with fresh or reused scratch.

## Compilation and storage

`Compile` accepts explicit positive limits for expression occurrences and instructions. Defaults are 4,096 occurrences and 16,384 instructions. Shared child expressions count once per occurrence. Exceeding a limit throws during compilation. The compiler traverses the normalized expression iteratively, and does not duplicate the right operand of XOR or equality into multiple control-flow branches.

Compiled instructions contain only opcodes and integer operands. Execution follows forward branches and never visits instructions in an unselected Boolean branch. The instruction count is an upper bound on interpreter steps; each predicate may have additional application-defined cost. A behavior-tree node or statechart guard evaluates its complete condition atomically with respect to its owner's scheduling budget.

`InstructionCount`, `ExpressionNodeCount` and `PredicateCount` expose diagnostics. PredicateCount includes syntactic occurrences in branches that may never run; it is an upper bound, not a required number of calls. Definitions allocate at construction and compilation. Evaluation never allocates its own storage.

`RequiredStackSize` is a static upper bound for temporary Boolean values along compiled paths. A single compiled predicate or constant has a direct fast path and requires zero scratch. All/Any reuse one value slot across children. XOR/equality preserve left operands while computing right operands. Callers can provide a stack span or allocate one reusable Boolean array per runner; no object is needed for each expression node per agent.

## Evaluators and concurrency

The generic `Evaluate(in context, ref evaluator, scratch)` overload uses a constrained interface call for struct evaluators and preserves their bookkeeping fields. The interface overload is convenient for a reference handler. Passing a new struct through the interface overload can box at the call site; use the generic ref overload for allocation-sensitive value evaluators.

Programs and expressions can be shared between threads. Each concurrent or nested call owns separate scratch and coordinates access to its evaluator and context. Keep scratch separate from context data and exclusively borrowed until the call returns. Scratch contents after success or failure are unspecified. Reusing one scratch span for reentrant evaluation can overwrite an outer evaluation's temporary values.

The library creates no worker, task, timer or global predicate-result cache. Hosts decide when a guard or condition node needs evaluation. Tests and benchmarks run on the managed host; validate the selected Unity/Godot player separately.

## Validation

[Tests](https://github.com/bezoro/csharp-framework/blob/0e73014d65f4c74f1842aac64a9be427dd3f0600/tests/Bezoro.Conditions.Tests/README.md) cover truth tables, exact predicate visit order, bounded/deep compilation, borrowed scratch, exceptions, concurrent definitions and warm allocation checks. [Benchmarks](https://github.com/bezoro/csharp-framework/blob/0e73014d65f4c74f1842aac64a9be427dd3f0600/benchmarks/Bezoro.Conditions.Benchmarks/README.md) use precompiled definitions and reusable scratch.

