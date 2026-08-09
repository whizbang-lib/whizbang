# Animated Visualizations

Interactive step-by-step animations of Whizbang event processing flows. Zero dependencies — open the HTML files directly in a browser.

## Animations

| File | Flow | Steps | Spec |
|------|------|-------|------|
| `07-end-to-end-flow.html` | Full command-to-query lifecycle across 6 phases | 18 | [spec](specs/07-end-to-end-flow.md) |
| `04-inbox-outbox-pattern.html` | Cross-service outbox/inbox with record field updates | 14 | [spec](specs/04-inbox-outbox-pattern.md) |
| `06-message-envelope-journey.html` | Hop-by-hop envelope inspector with scope deltas | 12 | [spec](specs/06-message-envelope-journey.md) |
| `process-work-batch-lifecycle.html` | C# orchestration around PostgreSQL process_work_batch | 20 | [spec](specs/process-work-batch-lifecycle.md) |
| `08-perspective-runner-replay.html` | Snapshot restore, event replay, late event rewind (3 modes) | 12 | [spec](specs/08-perspective-runner-replay.md) |
| `09-lifecycle-coordinator-whenall.html` | WhenAll pattern: parallel paths, stage transitions, PostLifecycle | 10 | [spec](specs/09-lifecycle-coordinator-whenall.md) |
| `10-policy-routing-engine.html` | Predicate matching, first-match-wins, topic/stream/partition | 7 | [spec](specs/10-policy-routing-engine.md) |
| `11-perspective-sync-append-wait.html` | Request-response over event sourcing, sync polling | 10 | [spec](specs/11-perspective-sync-append-wait.md) |
| `12-time-travel-debugging-rewind.html` | Late event detection, snapshot restore, UUID7 replay | 8 | [spec](specs/12-time-travel-debugging-rewind.md) |
| `13-source-generator-pipeline.html` | Syntactic filtering funnel, semantic analysis, code gen | 6 | [spec](specs/13-source-generator-pipeline.md) |
| `14-tag-hook-processing.html` | Tag discovery, priority-sorted hook chain, payload mutation | 7 | [spec](specs/14-tag-hook-processing.md) |
| `15-consistent-hashing-partitions.html` | Virtual partitions, heartbeat, failover, self-healing | 8 | [spec](specs/15-consistent-hashing-partitions.md) |

## How to View

Open any `.html` file directly in a browser:

```bash
open docs/diagrams/animations/07-end-to-end-flow.html        # macOS
xdg-open docs/diagrams/animations/07-end-to-end-flow.html    # Linux
start docs/diagrams/animations/07-end-to-end-flow.html       # Windows
```

No server, build step, or dependencies required.

## Controls

| Control | Action |
|---------|--------|
| Space | Play / Pause |
| Right Arrow | Step forward |
| Left Arrow | Step back |
| Home | Reset to beginning |
| Speed selector | 0.5x / 1x / 1.5x / 2x / 4x |
| Timeline segments | Click to jump to any step |

## Updating Animations After a Whizbang Release

Each animation has a spec file in `specs/` documenting its source references and maintenance triggers.

1. Check `specs/INDEX.md` — scan "Key Source Files" against the Whizbang changelog
2. For each affected animation, open its spec file and find the affected step in Section 4
3. Update the narration in the HTML file to match
4. Update `Last verified against source:` date in the spec file
5. Open the HTML in a browser and step through affected steps to verify

## Architecture

```
shared-styles.css          CSS variables, layout, node styles, animation utilities
animation-controller.js    Playback engine (AnimationController class)
*.html                     Self-contained animations linking shared CSS/JS
specs/                     Functional specs: source references, step descriptions, maintenance guides
```

Each animation defines its steps as an array of `{ id, label, narration, duration, onEnter, onExit }` objects. The `onEnter` callback adds CSS classes to DOM elements to trigger transitions.

## Modifying

- **Change narration text**: Edit the `narration` field in the step definition
- **Adjust timing**: Change `duration` (milliseconds) on each step
- **Add a step**: Add a new object to the `steps` array with `onEnter`/`onExit` callbacks
- **Change colors**: Edit CSS custom properties in `shared-styles.css`
- **Dark/light theme**: Handled automatically via `prefers-color-scheme`

## Source Accuracy

These animations reference actual Whizbang types. If the source changes, update the corresponding narration and field names:

- `MessageHop`, `HopType` — `src/Whizbang.Core/Observability/MessageHop.cs`
- `IMessageEnvelope` — `src/Whizbang.Core/Observability/IMessageEnvelope.cs`
- `OutboxRecord` — `src/Whizbang.Core/Messaging/OutboxRecord.cs`
- `InboxRecord` — `src/Whizbang.Core/Messaging/InboxRecord.cs`
- `LifecycleStage` — `src/Whizbang.Core/Messaging/LifecycleStage.cs`
- `DispatchModes` — `src/Whizbang.Core/Dispatch/DispatchMode.cs`
- `ScopeDelta` — `src/Whizbang.Core/Security/ScopeDelta.cs`
- `ProcessWorkBatchRequest`, `WorkBatch` — `src/Whizbang.Core/Messaging/IWorkCoordinator.cs`
- `WorkCoordinatorPublisherWorker` — `src/Whizbang.Core/Workers/WorkCoordinatorPublisherWorker.cs`
- `process_work_batch()` SQL — `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql`
- `IPerspectiveRunner` — `src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs`
- `IPerspectiveSnapshotStore` — `src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs`
- `ILifecycleCoordinator` — `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs`
- `AwaitPerspectiveSyncAttribute` — `src/Whizbang.Core/Perspectives/Sync/AwaitPerspectiveSyncAttribute.cs`
- `IMessageTagHook` — `src/Whizbang.Core/Tags/IMessageTagHook.cs`
- `compute_partition()` — `src/Whizbang.Data.Postgres/Migrations/001_CreateComputePartitionFunction.sql`
