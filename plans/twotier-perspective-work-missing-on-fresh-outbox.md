# Pre-existing failure: `ProcessWorkBatch_TwoTier_*` tests return empty `PerspectiveWork`

**Status**: open, pre-existing on `feature/receptor-firing-debug-logging` as of 2026-04-19
**Scope**: isolated — six consecutive `TwoTier_*` tests in `EFCoreRewindDetectionTests`, same signature
**Observed during**: dead-instance-tolerance fix (plan file `polymorphic-tumbling-moonbeam.md`) — the failure predates and is unrelated to that work; confirmed by running against a fully-unstashed baseline.

---

## Symptom

Every `ProcessWorkBatch_TwoTier_*` test in `tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreRewindDetectionTests.cs` fails deterministically in isolation:

```
TUnit.Engine.Exceptions.TestFailedException: AssertionException:
  Expected to contain <streamId>, because Small stream should be in the returned assignments
  but the item was not found in the collection
  at Assert.That(streamIds).Contains(streamId)
```

Each test:
- Registers a perspective association via `_registerMessageAssociationAsync("TestApp.Events.OrderCreatedEvent, TestApp", "perspective", "OrderListPerspective", "TestService")`.
- Dispatches N event outbox messages for a fresh stream via `NewOutboxMessages: [...]`.
- Expects `result.PerspectiveWork` to contain the stream.

Returns empty `PerspectiveWork`. So either Phase 4.5A isn't storing to `wh_event_store`, Phase 4.6 isn't creating `wh_perspective_events` rows, or Phase 7 isn't returning them.

Failed tests (all same signature):

- `ProcessWorkBatch_TwoTier_SmallStreamServedBeforeLargeStreamAsync`
- `ProcessWorkBatch_TwoTier_SmallStreamCompletesInOneTickAsync`
- `ProcessWorkBatch_TwoTier_LargeStreamStillServedAsync`
- `ProcessWorkBatch_TwoTier_LargeStreamCappedAtPerStreamLimitAsync`
- `ProcessWorkBatch_TwoTier_MultipleSmallStreamsFillFirstAsync`
- `ProcessWorkBatch_TwoTier_AllSmallStreams_NoTier2NeededAsync`

In-isolation run (3x): 3/3 fail. Full-suite run: the first TwoTier to run stops fail-fast.

## Why this is NOT the dead-instance-tolerance bug

Reproduced on a fully-unstashed working tree (no SQL edits, no C# rename) — same failure. So this is a separate pre-existing defect that lives on the branch. Confirming command:

```bash
git stash push -m "clean-baseline-check"
dotnet build tests/Whizbang.Data.EFCore.Postgres.Tests --force
cd tests/Whizbang.Data.EFCore.Postgres.Tests
dotnet run --no-build -- --treenode-filter '/*/*/*/ProcessWorkBatch_TwoTier*'
# expect: 6 failed, 0 passed
git stash pop
```

## Next-session starting points

1. **Is Phase 4.5A storing the event?** Instrument `wh_event_store` after the `ProcessWorkBatchAsync` call — expect 1 row per stream. If missing, the message_type may not pass the `is_event` check or normalization is producing an unexpected aggregate_type.
2. **Is Phase 4.6 creating the perspective event?** Check `wh_perspective_events` — expect 1 row per (stream, perspective). If missing, the join `es.event_type = ma.normalized_message_type` isn't matching — inspect what `normalize_event_type(...)` produces for `"TestApp.Events.OrderCreatedEvent, TestApp"` versus what `_registerMessageAssociationAsync` wrote into `wh_message_associations.normalized_message_type`.
3. **Is Phase 7 returning it?** If `wh_perspective_events` has the row but `result.PerspectiveWork` is empty, the Phase 7 return query is filtering it out — likely a mismatch between the test's `LeaseSeconds` (300) interacting with `v_lease_expiry`, or a `partition_number` / instance-rank check.
4. Compare against a known-passing perspective test (e.g., `ProcessWorkBatch_WithOutOfOrderEvent_SetsRewindRequiredOnCursorAsync`) which also uses `_registerMessageAssociationAsync` + `_createEventOutboxMessage` — it asserts on the cursor, not the returned work, so it may be passing because it never touches Phase 7.

## Scope boundary

This is a real bug, NOT a flake (consistent failure, no timing dependency). It needs its own plan. Don't try to fix it as part of the dead-instance-tolerance PR — that PR's tests (`ProcessWorkBatchAsync_FreshInboxOrphanedByDeadInstance_ClaimsOnNextTickAsync`, `ProcessWorkBatchAsync_FreshInboxWithLiveOwnerOnDifferentInstance_DoesNotClaimAsync`) pass cleanly in isolation.
