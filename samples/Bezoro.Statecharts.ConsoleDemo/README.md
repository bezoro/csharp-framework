# Statechart examples

Run the examples with:

```sh
dotnet run -c Release --project samples/Bezoro.Statecharts.ConsoleDemo
```

The project references Statecharts and Simulation; it uses no behavior trees. A chest keeps only a four-byte StateId and evaluates its two flat transitions on explicit signals. The character uses a hierarchical locomotion region and an orthogonal hit-reaction region. A grounded impact requests an additive animation while walking remains active. An airborne impact requests an impulse. The host accounts for health once per impact and owns the movement/animation command policy.

StateMachineBuilder and StatechartBuilder declare states, signals and containment directly, without coordinating numeric indices. Resolve construction handles once during setup; actors keep only the compiled definition and compact runtime identifiers. Multiple root regions declare parallel activity, and Region(parentState) declares hierarchy.

The perception example schedules one signal after 500 ms with the existing TickScheduler. Advancing the host clock does not evaluate the vision predicate until that signal is due. Leaving Patrolling stops rescheduling it; stale activation tokens are rejected even after reentering the same state. Cancel pending scheduler handles on exit when the host retains them, and still validate tokens for already-delivered asynchronous work.

Event, frame and interval checks use the same sparse signal transitions. Call Dispatch from the relevant input/gameplay event, dispatch a dedicated frame-check signal when the host needs it, or schedule that signal. A runner allocates no timer and creates no automatic update loop. The simple chest pays for none of the character or perception machinery.

Objects and immutable definitions are constructed during setup. State transitions and scheduled drains use preallocated storage. This example runs managed .NET code and does not launch Unity or Godot. Scene updates, physics impulses and animation layers belong to engine adapters.
