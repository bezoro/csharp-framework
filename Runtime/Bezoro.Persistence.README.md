# Bezoro.Persistence

Versioned save envelopes and a filesystem backend for serialized game state. The module has no Unity, Godot, JSON, or other package dependency. Applications choose serialization, encryption, compression, and schema migrations.

## Save and load

```csharp
using Bezoro.Persistence;

// Unity: Path.Combine(Application.persistentDataPath, "saves").
// Godot: ProjectSettings.GlobalizePath("user://saves").
var store = new AtomicFileSaveStore(saveDirectory,
    maximumPayloadBytes: 4 * 1024 * 1024);

byte[] encodedState = SerializeGameState();
await store.WriteAsync("profile-1", schemaVersion: 2,
    encodedState, cancellationToken);

byte[] buffer = new byte[4 * 1024 * 1024];
var result = await store.ReadAsync("profile-1", buffer,
    expectedSchemaVersion: 2, cancellationToken);
if (result.IsSuccess)
    RestoreGameState(buffer.AsSpan(0, result.BytesWritten));
```

The constructor resolves the save directory to an absolute path but creates no files or directories. The first write creates the directory. Slot names contain 1 to 64 lowercase ASCII letters, digits, underscores, or hyphens. Portable reserved filenames such as `con` and `com1` are rejected. A slot maps to `<slot>.bzsave`.

The caller owns the payload and destination memory. Keep a write's `ReadOnlyMemory<byte>` contents unchanged until `WriteAsync` completes. Do not concurrently access a read's destination until `ReadAsync` completes. `ReadOnlyMemory` is a borrowed view, not an immutable copy.

Consume the read buffer only when `IsSuccess` is true. Corruption, cancellation, or a schema mismatch may leave bytes in the destination; `BytesWritten` is zero on failure. An undersized destination returns `DestinationTooSmall` and a bounded `RequiredCapacity` without reading payload bytes. Its capacity information does not establish checksum validity.

`NotFound` is a normal result. Other expected data failures have distinct `SaveReadStatus` values. Actual filesystem errors and invalid caller arguments throw. `DeleteAsync` is idempotent and does not remove other slots. The store holds no open streams between calls and needs no disposal.

## Application schema versions

Envelope format and application schema are separate versions. Application schemas start at one. The default expected schema is null, which accepts any checksum-valid application schema and returns its version in `Header.SchemaVersion`. Use that mode to select an explicit migration after loading. Supplying an expected version returns `SchemaMismatch` when a valid save uses another version; the result then carries a checksum-validated header, but `IsSuccess` is false and its payload must not be consumed as the expected schema.

Serialize durable game data rather than process-local scheduler, entity, or subscription handles. Resolve runtime references using the application's persistent identifiers after loading. No runtime reflection or automatic type discovery is required by this module.

## Portable binary format

All integers use little-endian encoding. Format version 1 has a fixed 32-byte header:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | ASCII `BZRSAVE` followed by a zero byte |
| 8 | 2 | Envelope format version, currently `1` |
| 10 | 2 | Header size, currently `32` |
| 12 | 4 | Application schema version, greater than zero |
| 16 | 8 | Payload length in bytes |
| 24 | 4 | IEEE CRC-32 checksum |
| 28 | 4 | Reserved, must be zero |
| 32 | Payload length | Application bytes |

The CRC uses the reflected polynomial `0xEDB88320`, initial state `0xFFFFFFFF`, and final XOR `0xFFFFFFFF`. Its input is the concatenation of header bytes `0..23`, header bytes `28..31`, and the payload. Bytes `24..27` are excluded entirely, rather than included as zeros. Consequently, corruption of schema or length metadata is also covered when complete checksum validation runs. CRC is for accidental corruption detection; it is not authentication.

The standard `123456789` CRC vector is `0xCBF43926`. See the [CRC-32/ISO-HDLC catalogue](https://reveng.sourceforge.io/crc-catalogue/17plus.htm#crc.cat.crc-32-iso-hdlc). A checked-in envelope fixture was independently calculated with Python `struct` and `zlib`.

`SaveEnvelope.GetEncodedLength`, `Write`, and `Decode` operate on caller-owned spans. A warmed codec allocates no payload storage. `Write` supports overlapping source and destination spans. `Decode` rejects unsupported formats, malformed headers, oversized declarations, truncated payloads, trailing bytes, and incorrect checksums. Its successful payload span borrows the input's lifetime.

`DecodeHeader` is intended for adapters that read the fixed header before reserving payload capacity. It validates structure and the caller's explicit size limit only; it cannot validate a checksum without payload bytes. Both the codec and file backend validate the declared length before converting it to an integer or reading payload data. The maximum representable payload is `int.MaxValue - 32`, and applications should choose much smaller practical limits.

## Atomicity, cancellation, and concurrency

Each write creates a unique temporary file in the destination directory, writes the header and payload, flushes, closes the file, acquires the short commit gate, checks cancellation, and commits it. Existing slots use `File.Replace`; new slots use `File.Move`. Defensive handling of creation and deletion races retains up to eight bounded commit attempts. Persistent contention or filesystem errors fail with `IOException`. The implementation does not retry a failed Windows replacement indiscriminately, delete the old save first, or copy bytes over its contents.

On supported local filesystems, a reader sees the complete old envelope or the complete new envelope. Reads acquire their snapshot handle under the same short path gate used by commits and deletes, with `FileShare.ReadWrite | FileShare.Delete`. The gate is released immediately after the handle opens, before reading the header or payload. This avoids opening a Windows snapshot while replacement still owns its native handles; sharing write and delete access accommodates native replacement metadata operations. The store never modifies a committed envelope in place.

Concurrent writes on one store or separate instances use independent temporary files. Instances of this loaded module coordinate snapshot opens, commits, and deletes by normalized absolute path through 64 shared semaphore shards. Case-insensitive hashing covers Windows path casing; collisions only serialize extra boundary operations. This fixed-size coordination keeps no path dictionary and retains no path history. Checksumming, payload I/O, and flushing remain outside the gates, so already-open snapshots can be read concurrently with other operations.

The gate prevents overlapping Windows `File.Replace` calls and snapshot opens for the same path. Windows [`ReplaceFileW`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew) opens the replacement file with read/write/delete rights and no sharing mode; a new reader can otherwise encounter that transient ownership even though its eventual snapshot is read-only. Gate waits are asynchronous and honor cancellation. The last filesystem commit wins; invocation order does not define the winner. A concurrent delete may win after a successful write. Serialize operations in the application if command ordering matters. Multiple processes, separately loaded copies of the module, and filesystem aliases such as hard links, symbolic-link roots, or alternate long-path spellings require external coordination; path hashing does not establish file identity across those cases.

Deletion commits by renaming the live slot to a unique same-directory `.deleted.tmp` tombstone under its path gate, then deleting that retired file. This frees the live name immediately while existing snapshot handles continue reading their original envelope. On Windows, [deleting the live path directly](https://learn.microsoft.com/en-us/windows/win32/fileio/closing-and-deleting-files) can leave that name delete-pending until the last snapshot closes, preventing new opens or recreation. The tombstone keeps that pending state away from the live slot. Its final removal may wait for old snapshot handles to close.

Delete cancellation is checked immediately before the rename and not after it. If retired-file cleanup fails after removal, `DeleteAsync` throws an `IOException` explaining that the live slot was already removed and preserves the cleanup failure as its inner exception. It does not attempt to roll the deletion back over a later save. A crash between rename and cleanup can leave a retired tombstone; cleanup of abandoned files belongs to an application-controlled period with no active operations.

Cancellation is checked during checksum chunks, I/O, and immediately before commit attempts. A cancellation or failure before commit preserves an existing save and removes that operation's temporary file. A committed write returns success even if cancellation arrives afterward. If temporary-file removal itself fails, an `AggregateException` preserves both the write failure and cleanup failure. A process crash can leave abandoned temporary files; the store does not remove other operations' temporary files during normal execution.

By default, `flushToDisk` requests a synchronous `FileStream.Flush(true)` before committing. It can be disabled explicitly. That flush and checksumming may consume calling-thread time even though the API performs asynchronous I/O. Hosts with strict frame budgets should schedule the complete operation on a suitable data worker and keep engine object access on the engine thread. The store does not allocate a background task for each write step.

Atomic visibility depends on the filesystem and runtime implementation. A successful flush is not a portable guarantee against power loss, device failure, network-filesystem behavior, or directory-entry persistence. This backend is intended for a trusted, application-owned directory. It rejects symbolic-link slots where detected, but does not provide a sandbox against an actor racing filesystem metadata. It never interprets serialized bytes as executable objects.

## Unity and Godot

Runtime targets are .NET 10 and .NET Standard 2.1. Unity 6 consumes the precompiled .NET Standard assembly through the framework's UPM package. Godot .NET can use the corresponding managed assembly. The namespace contains no engine types.

Validate replacement and flush support on each intended player filesystem. WebGL, browser storage, and platform-specific console save APIs require an `ISaveStore` adapter; desktop file tests do not establish support for those platforms. The interface keeps serialization separate from the storage backend so an application can supply those adapters without replacing its data model.

## Validation and profiling

Tests cover external checksum/envelope vectors, overlap, corrupt metadata and payloads, size limits, schema handling, overwrite, borrowed-memory cancellation and failure during I/O, cleanup, and concurrent reads and writes. The I/O failure fixtures use a controlled `MemoryManager<byte>` rather than timing sleeps.

```sh
dotnet test tests/Bezoro.Persistence.Tests/Bezoro.Persistence.Tests.csproj -c Release
dotnet run -c Release --project benchmarks/Bezoro.Persistence.Benchmarks -- --filter '*SaveEnvelopeBenchmarks*'
```

Benchmarks measure checksum and envelope operations against reusable buffers. They do not claim filesystem latency, frame budgets, or power-loss durability.

