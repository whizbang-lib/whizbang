# Animation Spec Index

Quick-reference table for all 12 animations. When a Whizbang source file changes, scan the "Key Source Files" column to find which animations may need updating, then open the spec file for the affected step details.

| Animation | Spec | Steps | Key Source Files |
|-----------|------|-------|-----------------|
| End-to-End Flow | [spec](07-end-to-end-flow.md) | 18 | `IDispatcher.cs`, `IWorkCoordinator.cs`, `LifecycleStage.cs`, `OutboxRecord.cs`, `InboxRecord.cs`, `MessageEnvelope.cs`, `MessageDispatchContext.cs` |
| Inbox / Outbox Pattern | [spec](04-inbox-outbox-pattern.md) | 14 | `OutboxRecord.cs`, `InboxRecord.cs`, `LifecycleStage.cs`, `TransportConsumerWorker.cs`, `MessageDispatchContext.cs` |
| Message Envelope Journey | [spec](06-message-envelope-journey.md) | 12 | `IMessageEnvelope.cs`, `MessageHop.cs`, `MessageDispatchContext.cs`, `ScopeDelta.cs`, `IScopeContext.cs` |
| ProcessWorkBatch Lifecycle | [spec](process-work-batch-lifecycle.md) | 20 | `IWorkCoordinator.cs`, `029_ProcessWorkBatch.sql`, `WorkCoordinatorPublisherWorker.cs`, `IntervalWorkCoordinatorStrategy.cs`, `IWorkChannelWriter.cs` |
| Perspective Runner Replay | [spec](08-perspective-runner-replay.md) | 12 | `IPerspectiveRunner.cs`, `IPerspectiveSnapshotStore.cs`, `IEventStore.cs`, `IPerspectiveStore.cs` |
| Lifecycle Coordinator WhenAll | [spec](09-lifecycle-coordinator-whenall.md) | 10 | `ILifecycleCoordinator.cs`, `DispatchMode.cs`, `LifecycleStage.cs` |
| Policy Routing Engine | [spec](10-policy-routing-engine.md) | 7 | `PolicyContext.cs`, `IPolicyEngine.cs`, `PolicyConfiguration.cs`, `HashPartitionRouter.cs`, `MessageHop.cs`, `Dispatcher.cs` |
| Perspective Sync (AppendAndWait) | [spec](11-perspective-sync-append-wait.md) | 10 | `AwaitPerspectiveSyncAttribute.cs`, `IPerspectiveSyncAwaiter.cs`, `ILifecycleCoordinator.cs`, `IPerspectiveRunner.cs` |
| Time-Travel Debugging | [spec](12-time-travel-debugging-rewind.md) | 8 | `IPerspectiveRunner.cs`, `IPerspectiveSnapshotStore.cs`, `IEventStore.cs` |
| Source Generator Pipeline | [spec](13-source-generator-pipeline.md) | 6 | `src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs`, `ai-docs/performance-principles.md` |
| Tag Hook Processing | [spec](14-tag-hook-processing.md) | 7 | `IMessageTagHook.cs`, `IMessageTagRegistry.cs`, `TagContext.cs`, `LifecycleStage.cs` |
| Consistent Hashing Partitions | [spec](15-consistent-hashing-partitions.md) | 8 | `001_CreateComputePartitionFunction.sql`, `029_ProcessWorkBatch.sql`, `IWorkCoordinator.cs` (StaleThresholdSeconds) |

---

## Files That Affect Multiple Animations

| Source File | Animations Affected |
|-------------|---------------------|
| `src/Whizbang.Core/Observability/MessageHop.cs` | 06 (envelope journey), 07 (end-to-end), 10 (policy routing) |
| `src/Whizbang.Core/Observability/IMessageEnvelope.cs` | 06 (envelope journey), 07 (end-to-end), 04 (inbox/outbox) |
| `src/Whizbang.Core/Observability/MessageDispatchContext.cs` | 06 (envelope journey), 07 (end-to-end), 04 (inbox/outbox) |
| `src/Whizbang.Core/Messaging/LifecycleStage.cs` | 07 (end-to-end), 04 (inbox/outbox), 09 (WhenAll), 14 (tag hooks) |
| `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` | 07 (end-to-end), process-work-batch, 15 (partitions) |
| `src/Whizbang.Core/Perspectives/IPerspectiveRunner.cs` | 08 (runner replay), 11 (perspective sync), 12 (time-travel), 09 (WhenAll) |
| `src/Whizbang.Core/Lifecycle/ILifecycleCoordinator.cs` | 09 (WhenAll), 11 (perspective sync) |
| `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql` | process-work-batch, 15 (partitions) |

---

## Update Workflow

When Whizbang releases a new version:

1. Review the changelog for changes to any file in the "Key Source Files" columns above
2. For each affected animation, open its spec file
3. Find the affected steps in **Section 4** (Steps Specification) and update the narration text
4. Check **Section 5** (Maintenance Guide) for specific trigger conditions
5. Update `**Last verified against source:**` date in the spec frontmatter
6. Update the corresponding HTML animation file to match the new narration
7. Open the HTML in a browser and step through the affected steps to verify

## Known Stale Items

None as of 2026-04-05.
