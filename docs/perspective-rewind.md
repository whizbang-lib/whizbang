# Perspective Rewind

Rewind is the mechanism for handling out-of-order events in perspective processing. When events arrive with UUIDv7 IDs that are earlier than the cursor's position, the perspective runner replays events from a snapshot (or from the beginning) to ensure no events are permanently skipped.

## How It Works

### Detection (SQL — Phase 4.6B in `process_work_batch`)

When `process_work_batch` stores new events and creates perspective event work items:

1. Phase 4.6 creates perspective events via INSERT
2. **Phase 4.6B** checks: does any new event have `event_id < cursor.last_event_id`?
3. If yes: sets `status | 32` (RewindRequired flag) and `rewind_trigger_event_id` on the cursor

### Execution (C# — PerspectiveWorker)

1. Worker picks up work items with `RewindRequired` flag on the cursor
2. Acquires a distributed stream lock (prevents concurrent rewinds)
3. Calls `runner.RewindAndRunAsync(streamId, perspectiveName, triggeringEventId)`
4. Runner checks for snapshot before the triggering event
5. If snapshot found: replay from snapshot (partial replay)
6. If no snapshot: replay from event zero (full replay)
7. Single atomic write at end (model + checkpoint)
8. Lock released

### Snapshots

Snapshots are created automatically during normal processing every N events (default: 100). During rewind, the runner loads the latest snapshot before the triggering event and replays from there, avoiding a full replay from event zero.

## Configuration

### PerspectiveRewindOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | bool | `true` | Master switch for rewind detection and execution |
| `StartupScanEnabled` | bool | `true` | Scan for streams needing rewind on service startup |
| `StartupRewindMode` | enum | `Blocking` | `Blocking`: rewinds complete before serving. `Background`: rewinds run async |
| `MaxConcurrentRewinds` | int | `3` | Limit parallel rewind operations |

### PerspectiveSnapshotOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | bool | `true` | Whether snapshot creation is enabled |
| `SnapshotEveryNEvents` | int | `100` | Create snapshot every N events |
| `MaxSnapshotsPerStream` | int | `5` | Max snapshots retained per stream/perspective |

### PerspectiveStreamLockOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `LockTimeout` | TimeSpan | 30s | How long the distributed lock is valid |
| `KeepAliveInterval` | TimeSpan | 10s | How often the lock is renewed |

## Observability

### Log Entries

Two separate logger categories allow independent log level configuration:

**`Whizbang.Core.Workers.PerspectiveWorker`** — runtime rewind operations:

| Level | EventId | Message | When |
|-------|---------|---------|------|
| **Warning** | 52 | `Perspective rewind required for {PerspectiveName} stream {StreamId} — cursor at {CursorEventId}, late event {TriggerEventId} ({EventsBehind} events behind)` | Rewind detected |
| **Warning** | 53 | `Perspective rewind completed for {PerspectiveName} stream {StreamId} — replayed {EventsReplayed} events in {DurationMs}ms (from {ReplaySource})` | Rewind finished |
| **Warning** | 43 | `Failed to acquire stream lock for rewind on {PerspectiveName} stream {StreamId}, deferring` | Lock contention |

**`Whizbang.Core.Workers.PerspectiveStartupScan`** — startup scan (configure independently):

| Level | EventId | Message | When |
|-------|---------|---------|------|
| **Information** | 54 | `Startup rewind scan started: {StreamCount} streams require rewind across {PerspectiveCount} perspectives` | Scan begins |
| **Information** | 55 | `Startup rewind scan completed: {StreamCount} streams, {PerspectiveCount} perspectives rewound in {DurationMs}ms` | Scan finished |
| **Information** | 57 | `Startup rewind scan: no streams require rewind` | Clean startup |
| **Warning** | 56 | `Error during startup rewind scan — rewinds will be processed during normal polling` | Scan error |

Note: Individual perspective rewinds during startup go through the same code path as runtime rewinds and emit the same per-perspective Warning logs (EventId 52/53) from the `PerspectiveWorker` category.

Example configuration to see all startup scan messages:
```json
"Whizbang.Core.Workers.PerspectiveWorker": "Warning",
"Whizbang.Core.Workers.PerspectiveStartupScan": "Information"
```

### OTel Meters (Meter: `Whizbang.Perspectives`)

| Instrument | Type | Tags | Description |
|-----------|------|------|-------------|
| `whizbang.perspective.rewinds` | Counter | perspective_name, has_snapshot | Rewind operations triggered |
| `whizbang.perspective.rewind.duration` | Histogram (ms) | perspective_name | Replay duration |
| `whizbang.perspective.rewind.events_replayed` | Histogram | perspective_name | Events replayed per rewind |
| `whizbang.perspective.rewind.events_behind` | Histogram | perspective_name | Events behind cursor at trigger |

### OTel Span Tags (Activity: `Perspective RewindAndRunAsync`)

| Tag | Description |
|-----|-------------|
| `whizbang.perspective.name` | Perspective name |
| `whizbang.stream.id` | Stream being rewound |
| `whizbang.perspective.rewind_trigger_event_id` | Late event that triggered rewind |
| `whizbang.perspective.rewind.events_behind` | Count at trigger time |
| `whizbang.perspective.rewind.events_replayed` | Count after completion |
| `whizbang.perspective.rewind.has_snapshot` | Whether snapshot was available |
| `whizbang.perspective.rewind.replay_source` | "snapshot" or "full" |

### System Events

| Event | Properties | When |
|-------|-----------|------|
| `PerspectiveRewindStarted` | StreamId, PerspectiveName, TriggeringEventId, ReplayFromSnapshotEventId, HasSnapshot, StartedAt | Before replay begins |
| `PerspectiveRewindCompleted` | StreamId, PerspectiveName, TriggeringEventId, FinalEventId, EventsReplayed, StartedAt, CompletedAt | After replay finishes |
| `StreamRewindStarted` | StreamId, PerspectiveNames[], TriggerEventId, StartedAt | Before all perspective rewinds for a stream |
| `StreamRewindCompleted` | StreamId, PerspectiveNames[], TotalEventsReplayed, StartedAt, CompletedAt | After all perspective rewinds for a stream |

## Startup Scan

On service startup, the PerspectiveWorker scans `wh_perspective_cursors` for rows with the `RewindRequired` flag (`status & 32 = 32`). This catches rewinds that were flagged but never executed (e.g., service crashed before processing them).

- **Blocking mode** (default): Keeps processing work batches until all rewinds clear. Guarantees projections are repaired before serving reads.
- **Background mode**: Logs the summary and lets normal polling handle them. Faster startup but projections may be stale briefly.

## Related Files

| File | Purpose |
|------|---------|
| `src/Whizbang.Core/Perspectives/PerspectiveRewindOptions.cs` | Configuration |
| `src/Whizbang.Core/Perspectives/PerspectiveSnapshotOptions.cs` | Snapshot configuration |
| `src/Whizbang.Core/Events/System/SystemEvents.cs` | System events |
| `src/Whizbang.Core/Observability/PerspectiveMetrics.cs` | OTel meters |
| `src/Whizbang.Core/Workers/PerspectiveWorker.cs` | Worker implementation |
| `src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs` | Generated runner (RewindAndRunAsync) |
| `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql` | Phase 4.6B SQL |
| `src/Whizbang.Data.EFCore.Postgres/EFCorePerspectiveSnapshotStore.cs` | EFCore snapshot store |
