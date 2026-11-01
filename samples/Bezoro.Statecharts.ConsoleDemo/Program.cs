using System.Numerics;
using Bezoro.Conditions;
using Bezoro.Simulation.Timing;
using Bezoro.Statecharts;
using Bezoro.StateMachines;

ChestExample();
CharacterExample();
PollingExample();

static void ChestExample()
{
    var chest = new StateMachineBuilder();
    var closedState = chest.State();
    var openState = chest.State();
    var opening = chest.Signal();
    var closing = chest.Signal();
    chest.StartIn(closedState);
    chest.Transition(from: closedState, on: opening, to: openState);
    chest.Transition(from: openState, on: closing, to: closedState);
    var definition = chest.Build();
    StateId closed = chest.Resolve(closedState), open = chest.Resolve(openState);
    SignalId openSignal = chest.Resolve(opening), closeSignal = chest.Resolve(closing);
    // Each chest only owns this four-byte state. There is no per-frame runner or timer.
    StateId state = definition.InitialState;
    state = definition.Step(state, openSignal).CurrentState;
    Require(state == open, "The chest must open.");
    state = definition.Step(state, closeSignal).CurrentState;
    Require(state == closed, "The chest must close.");
    Console.WriteLine("Chest: Open -> Closed; only signals evaluate transitions.");
}

static void CharacterExample()
{
    var character = new StatechartBuilder();
    var locomotion = character.Region();
    var groundedState = locomotion.State();
    var inAirState = locomotion.State();
    locomotion.StartIn(groundedState);

    var groundMovement = character.Region(groundedState);
    var idleState = groundMovement.State();
    var walkingState = groundMovement.State();
    groundMovement.StartIn(idleState);
    var airMovement = character.Region(inAirState);
    var jumpingState = airMovement.State();
    var fallingState = airMovement.State();
    airMovement.StartIn(jumpingState);

    // This region is independent of locomotion and can react while movement continues.
    var reaction = character.Region();
    var normalState = reaction.State();
    var takingDamageState = reaction.State();
    reaction.StartIn(normalState);
    var damageReaction = character.Region(takingDamageState);
    var groundedFlinchState = damageReaction.State();
    var airborneKnockbackState = damageReaction.State();
    damageReaction.StartIn(groundedFlinchState);

    var movementEvent = character.Signal();
    var jumpEvent = character.Signal();
    var apexEvent = character.Signal();
    var landEvent = character.Signal();
    var damageEvent = character.Signal();
    var recoveredEvent = character.Signal();
    var moveGuard = ConditionExpression.All(ConditionExpression.Predicate(0),
        ConditionExpression.Not(ConditionExpression.Predicate(1))).Compile();
    var noMovement = ConditionExpression.Not(ConditionExpression.Predicate(0)).Compile();
    var groundedGuard = ConditionExpression.Predicate(2).Compile();
    var airGuard = ConditionExpression.Predicate(3).Compile();
    character.Transition(from: idleState, on: movementEvent, to: walkingState, guard: moveGuard);
    character.Transition(from: walkingState, on: movementEvent, to: idleState, guard: noMovement);
    character.Transition(from: groundedState, on: jumpEvent, to: inAirState);
    character.Transition(from: jumpingState, on: apexEvent, to: fallingState);
    character.Transition(from: inAirState, on: landEvent, to: groundedState);
    character.Transition(from: normalState, on: damageEvent, to: groundedFlinchState, guard: groundedGuard);
    character.Transition(from: normalState, on: damageEvent, to: airborneKnockbackState, guard: airGuard);
    character.Transition(from: takingDamageState, on: damageEvent, to: groundedFlinchState, guard: groundedGuard);
    character.Transition(from: takingDamageState, on: damageEvent, to: airborneKnockbackState, guard: airGuard);
    character.Transition(from: takingDamageState, on: recoveredEvent, to: normalState);
    var definition = character.Build();

    // Resolve once during setup. Execution keeps only the compact definition and identifiers.
    StateId grounded = character.Resolve(groundedState), inAir = character.Resolve(inAirState);
    StateId walking = character.Resolve(walkingState), jumping = character.Resolve(jumpingState);
    StateId normal = character.Resolve(normalState), takingDamage = character.Resolve(takingDamageState);
    StateId groundedFlinch = character.Resolve(groundedFlinchState), airborneKnockback = character.Resolve(airborneKnockbackState);
    SignalId movement = character.Resolve(movementEvent), jump = character.Resolve(jumpEvent), apex = character.Resolve(apexEvent);
    SignalId land = character.Resolve(landEvent), damage = character.Resolve(damageEvent), recovered = character.Resolve(recoveredEvent);
    var effects = new CharacterEffects(grounded, inAir, walking, groundedFlinch, airborneKnockback);
    var context = new CharacterContext { WantsMovement = true, Stunned = false, Health = 100 };
    var runner = new StatechartRunner<CharacterContext>(definition, effects);
    runner.Start(ref context);
    runner.Dispatch(movement, ref context);
    Require(runner.IsActive(grounded) && runner.IsActive(walking), "The character must be walking on the ground.");

    // Damage accounting occurs once per accepted impact, independently of reaction duration.
    context.Health -= 10;
    runner.Dispatch(damage, ref context);
    StateActivation oldReaction = runner.GetActivation(takingDamage);
    Require(runner.IsActive(walking) && runner.IsActive(groundedFlinch) && context.IsMoving,
        "An additive grounded hit must preserve walking.");
    Require(context.AdditiveHits == 1 && context.Impulse == Vector2.Zero, "The grounded hit must request only an additive reaction.");
    runner.Dispatch(recovered, ref context);
    Require(!runner.IsActive(oldReaction), "The completed reaction token must be stale.");
    runner.Dispatch(jump, ref context);
    Require(runner.IsActive(inAir) && runner.IsActive(jumping) && !runner.IsActive(grounded), "Jumping must leave the grounded subtree.");
    context.Health -= 10;
    runner.Dispatch(damage, ref context);
    Require(runner.IsActive(jumping) && runner.IsActive(airborneKnockback), "The airborne reaction must coexist with locomotion.");
    Require(context.Impulse == new Vector2(4, 8) && context.AdditiveHits == 1 && context.Health == 80,
        "The airborne impact must request an impulse once.");
    runner.Dispatch(apex, ref context);
    runner.Dispatch(land, ref context);
    Require(runner.IsActive(grounded) && runner.IsActive(airborneKnockback), "Landing and reaction recovery are independent policies.");
    runner.Dispatch(recovered, ref context);
    runner.Dispatch(movement, ref context);
    Require(runner.IsActive(walking) && runner.IsActive(normal), "The next grounded movement phase must resume.");
    runner.Stop(ref context);
    Console.WriteLine("Character: grounded hit preserved walking; airborne hit requested impulse; old activation rejected.");
}

static void PollingExample()
{
    var perception = new StatechartBuilder();
    var activity = perception.Region();
    var patrollingState = activity.State();
    var investigatingState = activity.State();
    var observation = perception.Signal();
    activity.StartIn(patrollingState);
    perception.Transition(from: patrollingState, on: observation, to: investigatingState,
        guard: ConditionExpression.Predicate(0).Compile());
    var definition = perception.Build();
    StateId patrolling = perception.Resolve(patrollingState), investigating = perception.Resolve(investigatingState);
    SignalId checkVision = perception.Resolve(observation);
    var handler = new VisionEffects();
    var context = new VisionContext { TargetVisible = true };
    var runner = new StatechartRunner<VisionContext>(definition, handler);
    runner.Start(ref context);
    StateActivation initialPatrol = runner.GetActivation(patrolling);
    var scheduler = new TickScheduler<VisionPoll>(capacity: 1);
    long interval = TimeSpan.FromMilliseconds(500).Ticks;
    scheduler.Schedule(new VisionPoll(initialPatrol, checkVision), interval);
    Span<ScheduledItem<VisionPoll>> due = stackalloc ScheduledItem<VisionPoll>[1];
    for (int frame = 1; frame <= 60; frame++)
    {
        long now = TimeSpan.FromMilliseconds(frame * 10).Ticks;
        SchedulerDrainResult batch = scheduler.AdvanceTo(now, due);
        if (frame < 50) Require(handler.SightChecks == 0, "Vision must not be evaluated before its scheduled interval.");
        for (int i = 0; i < batch.Count; i++)
        {
            VisionPoll poll = due[i].Value;
            if (!runner.IsActive(poll.Activation)) continue;
            runner.Dispatch(poll.Signal, ref context);
            // Missed polls are coalesced into one observation. No catch-up loop over old world snapshots.
            if (runner.IsActive(poll.Activation)) scheduler.Schedule(poll, interval);
        }
    }
    Require(handler.SightChecks == 1 && runner.IsActive(investigating) && scheduler.Count == 0,
        "The single observation must transition and stop further patrol polling.");
    runner.Stop(ref context);
    runner.Start(ref context);
    Require(runner.IsActive(patrolling) && !runner.IsActive(initialPatrol),
        "Reentering the same state must not resurrect an old scheduled activation.");
    runner.Stop(ref context);
    Console.WriteLine("Perception: one check at 500 ms; no frame-by-frame guard polling; old scheduled activation rejected.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class CharacterContext
{
    public bool WantsMovement;
    public bool Stunned;
    public bool IsMoving;
    public int Health;
    public int AdditiveHits;
    public Vector2 Impulse;
}

sealed class CharacterEffects : StatechartHandler<CharacterContext>
{
    private readonly StateId _grounded, _inAir, _walking, _groundedFlinch, _airborneKnockback;
    public CharacterEffects(StateId grounded, StateId inAir, StateId walking, StateId groundedFlinch, StateId airborneKnockback)
    { _grounded = grounded; _inAir = inAir; _walking = walking; _groundedFlinch = groundedFlinch; _airborneKnockback = airborneKnockback; }
    public override bool EvaluateGuard(int predicateId, StatechartView configuration, in CharacterContext context) => predicateId switch
    {
        0 => context.WantsMovement, 1 => context.Stunned, 2 => configuration.IsActive(_grounded),
        3 => configuration.IsActive(_inAir), _ => throw new InvalidOperationException("Unknown predicate.")
    };
    public override void OnEnter(StateId state, StateActivation activation, ref CharacterContext context)
    {
        if (state == _walking) context.IsMoving = true;
        if (state == _groundedFlinch) context.AdditiveHits++;
        if (state == _airborneKnockback) context.Impulse = new Vector2(4, 8);
    }
    public override void OnExit(StateId state, StateActivation activation, ref CharacterContext context)
    { if (state == _walking) context.IsMoving = false; }
}

readonly record struct VisionPoll(StateActivation Activation, SignalId Signal);
struct VisionContext { public bool TargetVisible; }
sealed class VisionEffects : StatechartHandler<VisionContext>
{
    public int SightChecks { get; private set; }
    public override bool EvaluateGuard(int predicateId, StatechartView configuration, in VisionContext context)
    {
        if (predicateId != 0) throw new InvalidOperationException("Unknown vision predicate.");
        SightChecks++;
        return context.TargetVisible;
    }
}
