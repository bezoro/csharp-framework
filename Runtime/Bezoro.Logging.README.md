# Bezoro.Logging

Conditional logging with typed payloads, filtering, formatting, and optional metadata for .NET and game-engine consumers.

## Behavior

- **Conditional log calls**: `Logger.Log*` calls and their arguments are omitted at call sites compiled without `DEBUG`
- **Event-driven architecture**: Subscribe to `Logger.OnLog` to handle log output
- **Rich metadata**: Timestamps, sequence numbers, thread IDs, file locations, and caller info
- **Category system**: 50+ predefined categories for game systems, networking, UI, and more
- **Async context tracking**: Automatic flow through async/await boundaries
- **Performance timing**: Scoped elapsed-time measurement when the Bezoro.Logging assembly is compiled with `DEBUG`
- **Flexible grouping**: Group logs by caller, category, thread, time window, or async context
- **Visual styling**: Configurable colors and emoji indicators per log level
- **Unity compatible**: Targets both .NET 10 and .NET Standard 2.1

## Installation

Add a reference to `Bezoro.Logging` in your project:

```xml
<ProjectReference Include="path/to/Bezoro.Logging.csproj" />
```

## Quick start

```csharp
using Bezoro.Logging;
using Bezoro.Logging.Types;

// Subscribe to log events
Logger.OnLog += payload => Console.WriteLine(payload.FormattedMessage);

// Basic logging
Logger.Log("Application started");
Logger.Log("Player joined", LogLevel.Success, LogCategory.Network);
Logger.Log("Low memory warning", LogLevel.Warning, LogCategory.Memory);
Logger.Log("Connection failed", LogLevel.Error, LogCategory.Network);

// Exception logging with automatic detail extraction
try
{
    // ... code that throws
}
catch (Exception ex)
{
    Logger.Log(ex, "Failed to load configuration", LogCategory.Configuration);
}
```

## Log levels

| Level       | Description                               | Default Color   |
|-------------|-------------------------------------------|-----------------|
| `Info`      | General informational messages            | White           |
| `Success`   | Successful operation notifications        | Green           |
| `Warning`   | Potential issues or non-critical problems | Yellow          |
| `Error`     | Problems that don't stop execution        | Red             |
| `Exception` | Severe errors with stack trace capture    | Dark Red (Bold) |

## Categories

Bezoro.Logging includes 50+ predefined categories organized by domain:

- **Core**: `Default`, `System`, `Debug`, `Test`, `Profiling`
- **Game Systems**: `Gameplay`, `Combat`, `Inventory`, `Quest`, `Dialog`, `Ai`
- **Graphics**: `Rendering`, `Shaders`, `Particles`, `Lighting`, `PostProcessing`
- **Infrastructure**: `Network`, `Database`, `FileIo`, `Memory`, `Security`
- **UI/Input**: `Ui`, `Input`, `Camera`
- **Audio**: `Audio`
- **And more**: `Loading`, `SaveSystem`, `Resources`, `Localization`, `Authentication`

## Configuration

### Global settings

```csharp
// Enable/disable all logging
LoggerSettings.Enabled = false;

// Set minimum log level
Logger.MinimumLevel = LogLevel.Warning;

// Mute specific categories
LoggerSettings.MutedCategories.Add(LogCategory.Debug);
LoggerSettings.MutedCategories.Add(LogCategory.Profiling);
```

Messages with no subscribers, disabled logging, below-minimum levels, or muted categories return before message formatting, collection enumeration, metadata providers, or stage tracking. Call-site expressions still execute whenever `DEBUG` is defined: `Logger.Log(BuildExpensiveMessage())` calls the builder before the logger can inspect its filter. Conditional compilation removes the whole call, including its arguments, when that call site's compilation omits `DEBUG`. Guard expensive builders with `if (Logger.IsEnabled(LogLevel.Info, LogCategory.Loading))` when runtime filters also need to skip argument construction. `IsEnabled` is a snapshot of filters and subscriber presence.

### Metadata options

```csharp
// Timestamp configuration
LoggerSettings.Timestamp = TimestampConfig.Default;           // HH:mm:ss.fff
LoggerSettings.Timestamp = TimestampConfig.Create("yyyy-MM-dd HH:mm:ss");
LoggerSettings.Timestamp = TimestampConfig.Disabled;

// Sequence numbers (for log ordering)
LoggerSettings.SequenceNumber = SequenceNumberConfig.On;
LoggerSettings.SequenceNumber = SequenceNumberConfig.Off;

// Thread ID tracking
LoggerSettings.ThreadId = ThreadIdConfig.On;
LoggerSettings.ThreadId = ThreadIdConfig.Off;

// File location in logs
LoggerSettings.FileLocation = FileLocationConfig.FullPath;
LoggerSettings.FileLocation = FileLocationConfig.FilenameOnly;
LoggerSettings.FileLocation = FileLocationConfig.Disabled;

// Use a caller-thread-safe provider. Update this cached value on the engine thread.
LoggerSettings.FrameCount = FrameCountConfig.Create(() => Volatile.Read(ref cachedFrameCount));
```

### Log grouping

```csharp
// Group logs by various criteria
LoggerSettings.Grouping = GroupingConfig.Create(LoggerSettings.ContextGrouping.CallerType);
LoggerSettings.Grouping = GroupingConfig.Create(LoggerSettings.ContextGrouping.Category);
LoggerSettings.Grouping = GroupingConfig.Create(LoggerSettings.ContextGrouping.Thread);
LoggerSettings.Grouping = GroupingConfig.Create(LoggerSettings.ContextGrouping.AsyncContext);

// Time-window grouping
LoggerSettings.Grouping = GroupingConfig.Create(
    LoggerSettings.ContextGrouping.TimeWindow,
    timeWindowMs: 1000); // 1 second windows
```

### Visual styling

```csharp
// Customize colors per log level
LoggerSettings.InfoStyle = new LogStyle(ConsoleColor.Cyan);
LoggerSettings.ErrorStyle = new LogStyle(ConsoleColor.Red, bold: true);
LoggerSettings.WarningStyle = new LogStyle(ConsoleColor.Yellow, italic: true);
```

## Performance timing

Measure operation durations with a scoped timer:

```csharp
using (Logger.BeginTimer("LoadLevel", LogCategory.Loading))
{
    // ... loading code ...
}
// Output includes: LoadLevel completed in 123.45ms
```

`Logger.BeginTimer` itself is not conditional. When the Bezoro.Logging assembly is
compiled with `DEBUG`, the returned `PerformanceTimer` captures a start timestamp
when the Info level and category have an active sink, and logs from `Dispose` if still enabled. A filtered timer or `default(PerformanceTimer)` does no disposal work. Otherwise, a Release timer stores no state and `Dispose` performs
no work. Direct `Logger.Log*` calls are conditional and are omitted when `DEBUG` is
not defined in the compilation containing each call site.

## Async context tracking

Track hierarchical context through async/await:

```csharp
using (LoggerSettings.BeginAsyncContext("GameLoop"))
{
    Logger.Log("Starting frame"); // Context: GameLoop

    using (LoggerSettings.BeginAsyncContext("Player-123"))
    {
        await ProcessPlayerAsync();
        Logger.Log("Player updated"); // Context: GameLoop > Player-123
    }
}
```

## Log output format

Depending on the enabled settings, an ordinary formatted message follows this structure:

```
🔄 [root > child] (when an async context is present)
[#sequence timestamp Fframe Tthread G:group] severity-emoji [category-emoji] Message
  └─ file :: caller
```

For example, with sequence, timestamp, and thread metadata enabled:
```
[#42 14:32:15.123 T1] ℹ️ [🌐] Player connected
  └─ GameManager.cs :: GameManager.OnPlayerJoin()
```

With async context:
```
🔄 [GameLoop > Player-123]
[#43 14:32:15.456 T1] ℹ️ [🧠] Computing path
  └─ AIController.cs :: AIController.ComputePath()
```

The detail line appears only when caller information is captured or the level is
`Warning`, `Error`, or `Exception`, and it contains only the enabled, available values.

## LogPayload

When handling log events, you receive a `LogPayload` with:

| Property | Description |
| --- | --- |
| `Timestamp` | UTC timestamp captured for the event |
| `Level` | Log severity level |
| `Category` | Optional category |
| `Message` | Raw message content |
| `FormattedMessage` | Message formatted according to the captured settings |
| `SeverityEmoji` | Emoji for the level |
| `CategoryEmoji` | Emoji for the category, when present |
| `CallerInfo` | Captured `TypeName.MethodName()` value, when requested |
| `ContextObject` | Associated object, such as a Unity object |
| `Style` | Visual styling hints captured for the level |
| `Exception` | Original exception instance, when logging an exception |
| `ExceptionType` | Exception type name |
| `InnerExceptionType` | Inner exception type name, when present |
| `InnerExceptionMessage` | Inner exception message, when present |
| `StackTrace` | Captured exception stack trace |
| `AsyncContext` | Async hierarchy joined with ` > ` |
| `AsyncContextHierarchy` | Ordered async-context names |
| `AsyncContextDepth` | Number of names in the async hierarchy |
| `GroupingContext` | Grouping identifier computed from `LoggerSettings.Grouping` |
| `Stage` | Normalized stage label, when stage tracking is enabled |

## API reference

| API | Purpose |
| --- | --- |
| `Logger` | Emits conditional log records, exceptions, and performance timings through `OnLog`. |
| `LoggerSettings` | Configures enablement, metadata, grouping, styling, and async context. |
| `LogPayload` | Carries required constructor values and optional init-only metadata delivered to subscribers. Referenced objects and collections are not guaranteed to be immutable. |
| `LogLevel` / `LogCategory` | Classifies severity and subsystem. |
| `GroupingConfig` | Selects caller, category, thread, async-context, or time-window grouping. |
| `PerformanceTimer` | Measures and logs a scoped operation duration when the Bezoro.Logging assembly is compiled with `DEBUG`. |

## Unity 6 and Godot integration

Use the precompiled .NET Standard 2.1 DLL in Unity 6. Logging invokes `OnLog` synchronously on the caller's thread. A subscriber must dispatch scene-object access to the engine thread when producers log from workers. This Unity sink queues payloads and drains up to 100 per frame:

```csharp
using System.Collections.Concurrent;
using Bezoro.Logging;
using Bezoro.Logging.Types;
using UnityEngine;

public sealed class UnityLogSink : MonoBehaviour
{
    private readonly ConcurrentQueue<LogPayload> _pending = new ConcurrentQueue<LogPayload>();

    private void OnEnable() => Logger.OnLog += Receive;
    private void OnDisable() => Logger.OnLog -= Receive;
    private void Receive(LogPayload payload) => _pending.Enqueue(payload);

    private void Update()
    {
        for (int i = 0; i < 100 && _pending.TryDequeue(out var payload); i++)
        {
            var context = payload.ContextObject as UnityEngine.Object;
            switch (payload.Level)
            {
                case LogLevel.Error:
                case LogLevel.Exception:
                    Debug.LogError(payload.FormattedMessage, context);
                    break;
                case LogLevel.Warning:
                    Debug.LogWarning(payload.FormattedMessage, context);
                    break;
                default:
                    Debug.Log(payload.FormattedMessage, context);
                    break;
            }
        }
    }
}
```

The sink's queue is unbounded. An application that logs faster than it drains should add its own capacity or drop policy. Unsubscribe static handlers when their owner is disabled or destroyed; stop or coordinate producers before tearing down the sink. Godot integrations can drain an equivalent queue during `_Process` and call their chosen Godot logging APIs there.

Metadata providers also execute on the logging caller's thread. Use cached frame or scene values when logging from workers. The framework does not dispatch providers to the main thread.

## Target frameworks

- .NET 10
- .NET Standard 2.1 (Unity)

## Runtime notes

- `Logger.Log*` calls use `Conditional("DEBUG")`; each call is controlled by the `DEBUG` symbol in the compilation containing that call site.
- `Logger.BeginTimer` remains a normal call in all builds, while its timer captures and logs only when the Bezoro.Logging assembly is compiled with `DEBUG`.
- Output is event-driven; applications own console, file, Unity, or telemetry sinks.
- Async context flows through `AsyncLocal`. Create and dispose nested scopes in their owning async flow and in reverse creation order. Ordinary sibling async branches maintain separate current contexts. Disposing a scope in a child flow does not mark the parent flow's scope disposed. Out-of-order disposal in the same flow throws before changing its context. Repeated disposal in a flow where the scope is no longer current is a no-op. Context names must contain a non-whitespace character.
- `Logger` owns filtering, stage transitions, sequence/time/thread/frame capture, and synchronous dispatch; the internal formatter deterministically maps captured input, settings, and context into `LogPayload`.
- Configuration and the subscriber list are process-global. Set configuration before concurrent logging, or synchronize changes externally, especially mutation of `MutedCategories`.
- Subscriber and metadata-provider exceptions propagate to the logging caller. Wrap application sinks when a sink failure must not interrupt gameplay.
- Disabled sequence metadata does not advance its shared counter. Disabled thread metadata avoids thread-id lookup unless thread grouping needs it. Filename extraction runs only for messages that include a detail line.
- Formatting retained logs, collection arguments, and async-context hierarchies can allocate. This logger does not promise zero allocation.
- Timers compiled in Release without `DEBUG` do not collect timings. Direct calls made by a caller that defines `DEBUG` still invoke the library even when its DLL was built in Release.

## Migration

`LogLevel.Success` now sorts below `Warning`. Its numeric value and the numeric values of `Warning`, `Error`, and `Exception` changed; serialize level names or update stored numeric values. A warning-or-higher minimum suppresses success messages.

`LogPayload` now takes its four required values in `new LogPayload(level, message, formattedMessage, severityEmoji)`. These properties are get-only; optional metadata remains init-only. This constructor works in C# 9 consumers of the precompiled DLL and rejects null display values or undefined levels. There is no C# 11 `required` construction barrier.

`GroupingConfig` now uses validated factories and get-only properties. Use `GroupingConfig.Create`; time-window grouping requires positive milliseconds and cannot divide by zero during logging. Unobserved log calls no longer execute providers or advance stage tracking. To prime stage state deliberately, register a sink before logging. Null object messages format as `"null"`.

## Build and validation

Use the SDK selected by [global.json](https://github.com/bezoro/framework/blob/e7472a00169f392e3724e170581fe384c0e024ef/global.json):

```bash
dotnet build src/Bezoro.Logging/Bezoro.Logging.csproj -c Release
dotnet test tests/Bezoro.Logging.Tests/Bezoro.Logging.Tests.csproj -c Release -f net10.0
```

The test project defines `DEBUG` so conditional log calls are exercised in Release tests. See [the Logging tests](https://github.com/bezoro/framework/blob/e7472a00169f392e3724e170581fe384c0e024ef/tests/Bezoro.Logging.Tests/README.md).

