# Multi-stream fan-out: one foreign event, many rows (#2275)

Status: planned (2026-11-26). Design decided by the owner on 2026-11-25. No slice started. Release: 1.0 (Release 1.0
Planning board). Tracking: #2275 (question, accepted); each slice below is drafted to be filed as its own issue.

## Decision

The owner decided on 2026-11-25 (ET): "the recommendation summary as written"
([decision comment](https://github.com/whizbang-lib/whizbang/issues/2275#issuecomment-7169844787)), on the
[design discussion](https://github.com/whizbang-lib/whizbang/issues/2275#issuecomment-7066289504) that followed the
2026-11-21 direction ([comment](https://github.com/whizbang-lib/whizbang/issues/2275#issuecomment-7066158249)):

| Question | Decision | Why (from the design discussion) |
|---|---|---|
| Scope | Option C for 1.0, built on the collective executor and the collective replay applier | Reuses the set-update and replay machinery that exists; no new write-side events |
| Ordering | Replay merges a row's own stream and the foreign events that target it, by commit order | Commit order is already global; late renames come out right on live, rewind and rebuild |
| Targets | A declared, indexed key column on the model, not an arbitrary expression | Index-backed live updates and index-backed replays; the expression form makes a rebuild scan every foreign event |
| Deletion | A foreign delete marks or clears through setters; no purge of targeted rows | Uses what exists; purge is the one action that makes replay ordering hard |
| Received events | Use the replay order #1627 / #1629 settle on (`ReplayEventOrder`) | One order rule for received events, not two |

The rest of the design discussion is part of the decision as well: the key form "(a) by key" is the 1.0 form and "(b) by
predicate" waits for a real case; a foreign event that arrives late rewinds each row it targets, by the same rule a row's
own late events follow; and the setters fall under the same purity rule as collective specs (WHIZ106).

Not in 1.0: the predicate form of `Targets`, a purge action for targeted rows, and any write-side fan-out event (option B
in the issue). Each would be a new question issue if a real case needs it.

## What it is

A collective event is a set update the **producer** declares: the event carries its scope, and the framework turns the
scope into one `UPDATE ... WHERE`. A fan-out is the same set update declared by the **consuming perspective**: "when
`CustomerRenamed` arrives, the rows of mine whose `CustomerId` is that customer change." The producer writes nothing new;
the foreign aggregate's own events are the broadcast.

```csharp
public class OrderSummaryProjection
  : IPerspectiveFor<OrderSummary, OrderPlaced, OrderShipped>,
    IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>,
    IPerspectiveFanOutFor<OrderSummary, CustomerDeleted> {

  // The rows each foreign event targets: those whose CustomerId equals the event's stream id
  public static FanOutKey<OrderSummary> Targets => FanOut.By(o => o.CustomerId);

  public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) =>
    new CollectiveSpec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));

  public ICollectiveSpec<OrderSummary> Apply(CustomerDeleted e) =>
    new CollectiveSpec<OrderSummary>(s => s.SetProperty(o => o.CustomerDeleted, true));
}
```

Names are working names from the design discussion. One shape change from the sketch there: the sketch's
`void Set(ICollectiveSetters<T> s, TEvent e)` becomes `ICollectiveSpec<T> Apply(TEvent e)`, because both drivers compile
the setters from an expression tree (`CollectiveSettersRewriter` for EF Core, `DapperCollectiveSpecCompiler` for
Dapper), which is the shape `[CollectiveApplyFor]` handlers already return. The member is named `Apply`, not the sketch's `Set`
(owner, 2026-11-27): every other member that turns an event into a row change is `Apply`, and `Apply(TEvent)` overloads
cleanly beside `IPerspectiveFor.Apply(model, event)`. A perspective that needs a different key per
foreign event implements `Targets` explicitly per interface
(`static FanOutKey<OrderSummary> IPerspectiveFanOutFor<OrderSummary, RegionRenamed>.Targets => FanOut.By(o => o.RegionId)`).

### How it runs

- **Live.** The foreign event is stored in this service's event store as any received or local event is. A fan-out
  association routes it to the collective sink (`CollectiveRouting.SINK_PERSPECTIVE_NAME`, `__collective__`), where
  `PerspectiveWorker` dispatches it through `ICollectiveDispatcher` and `ICollectiveEventExecutor`: one batched
  `UPDATE order_summary SET ... WHERE customer_id = @streamId` within the event's tenant, with the apply lock
  (`CollectiveApplyLockKey`), the resumable progress (#1969) and the reach record (#1970) collectives already have.
- **Rebuild and rewind.** `PerspectiveRunnerBase` asks the replay applier for the row's fan-out events: for each key
  value the row holds during the fold, the events of the fan-out types on the stream whose id is that value, read by
  `(stream_id, version)`, in the row's tenant. They are merged with the row's own events in commit order
  (`ReplayEventOrder`) and folded in memory through `ICollectiveInMemoryExecutor`, each one applying only while the
  row's key equals the event's stream id.
- **Late events.** A foreign event applied live to a row that has already folded a later event of its own rewinds that
  row, and a row folding its own event merges the fan-out events of its key that the sink already applied after it.

## Code it touches

| Area | Files and types |
|---|---|
| Contract | new `IPerspectiveFanOutFor<TModel, TEvent>`, `FanOutKey<TModel>`, `FanOut.By` in `src/Whizbang.Core/Perspectives/`; reuses `ICollectiveSpec<TModel>` and `ICollectiveSetters<TModel>` |
| Registration entry | `src/Whizbang.Core/Perspectives/CollectiveApplyEntry.cs` (a fan-out entry carries its key column and a `SpecKind` for fan-out; `EntryKey` keeps progress per handler) |
| Generator | `src/Whizbang.Generators/CollectiveApplyDiscoveryGenerator.cs`, `CollectiveApplyShape.cs`, `PerspectiveRunnerGenerator.cs` and `Templates/PerspectiveRunnerClassTemplate.cs` (the runner learns its fan-out types and key columns) |
| Analyzers | `src/Whizbang.Generators/PerspectivePurityAnalyzer.cs` (WHIZ106 covers the fan-out `Apply`), `src/Whizbang.Generators/Analyzers/CollectiveApplyShapeAnalyzer.cs`, `Analyzers/PerspectiveFieldAnalyzer.cs` (key column rules); new diagnostic ids from the registry checked by `.github/scripts/Test-DiagnosticIds.ps1` |
| Routing | `src/Whizbang.Core/Perspectives/CollectiveRoutingAssociations.cs` (fan-out association entries and the associations hash), the association startup step that writes `wh_message_associations` |
| Live dispatch | `src/Whizbang.Core/Workers/PerspectiveWorker.cs` (the sink path: `_loadCollectiveSinkBatchAsync`, the `DispatchAsync` loop, which casts to `ICollectiveEvent` today), `src/Whizbang.Core/Perspectives/CollectiveDispatcher.cs`, a new `FanOutScopeResolver : ICollectiveScopeResolver` |
| Drivers | EF Core: `src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveEventExecutor.cs`, `CollectiveEventApplier.cs`, `EFCoreCollectiveAdapter.cs`; Dapper: `src/Whizbang.Data.Dapper.Postgres/Collective/DapperCollectiveEventExecutor.cs`, `DapperCollectiveEventApplier.cs`, `DapperCollectiveSpecCompiler.cs`; shared: `src/Whizbang.Data.Postgres/Collective/CollectivePredicateSqlCompiler.cs`, `CollectivePhysicalColumns.cs`, `CollectiveApplyLockKey.cs` |
| Replay | `src/Whizbang.Core/Perspectives/ICollectiveReplayApplier.cs` (a new default member, so existing implementations and fakes keep compiling), `src/Whizbang.Data.Postgres/Collective/CollectiveReplayApplier.cs`, `CollectiveInMemoryEvaluator.cs`, `ArchivedCollectiveStreams.cs`; `src/Whizbang.Core/Perspectives/PerspectiveRunnerBase.cs` (`_foldAsync`, `_foldCollective`, the rewind run's `InterleaveAfterAsync` call and `_collectivesThroughBoundaryAsync`), `ReplayEventOrder.cs`, `PerspectiveRebuilder.cs` |

## Migrations and hot paths

- **Routing without a new branch in the emit chains (preferred).** Both emit chains
  (`_emit_event_store_chain`, latest in `573_StreamsAreVersionedInEmissionOrder.sql`, and
  `_emit_event_store_chain_for_inbox`, latest in `574_ReceivedStreamsAreVersionedInReceiveOrder.sql`) already insert
  one `wh_perspective_events` row per `(event, wh_message_associations.target_name)` for association type
  `perspective`. A fan-out association written as type `perspective` with target `__collective__` gets the event its
  sink row from that insert, with no SQL change and no extra statement for an ordinary event. Slice F2 proves this
  against every SQL path that treats `__collective__` specially (`wh_rewind_reap_floor` and
  `_wh_rewind_perspectives_from` in 600, the straggler reconcile in 574, `_wh_collective_row_wanted` in 391). If any
  of them breaks, the fallback is a new migration that adds a fan-out insert behind a per-batch flag, the way
  `v_any_collective` gates the collective insert, measured first on a copied table.
- **Rewind of touched rows (F5)** likely needs one new SQL function that flags the cursors of the rows an `UPDATE`
  touched whose last applied event is newer than the fan-out event, reusing `_wh_rewind_perspectives_from`. New
  migration, next free number above develop (626 today) and every open pull request at the time.
- **No new index on the event store.** Replay reads foreign streams by `(stream_id, version)`, which exists. The key
  column's index on the perspective table is an application schema change the generator emits from `[Indexed]`.
- **Hot paths.** The generated standard apply is the hottest code in the framework. Anything F5 adds to it (the per-key
  lock) is emitted only for models that declare a fan-out, and a test proves a model without one issues no extra
  statement. The emit chains are untouched under the preferred routing.

## Slices

Each slice is one issue, red before green (`ai-docs/tdd-strict.md`), 100% line and branch coverage of new code, no
reflection, both drivers where it applies, and its docs in the same change. Tests are signal-based: every interleaving
below is produced by holding a worker at a named point, never by a delay.

### F1. Contract, discovery and build-time rules

- **Changes.** `IPerspectiveFanOutFor<TModel, TEvent>`, `FanOutKey<TModel>`, `FanOut.By`. The generator discovers each
  fan-out, extracts the key member from the `FanOut.By` lambda at compile time, and emits a registration entry beside
  the `[CollectiveApplyFor]` entries. New analyzer errors: the key is not a declared physical field; the key has no
  `[Indexed]`; the key's type cannot hold a stream id (`Guid` or a strongly typed id over one); a fan-out `Apply` assigns the key
  column (keys come only from the row's own events, which is what makes the replay's key discovery sound); a fan-out `Apply`
  references anything but the row and the event (WHIZ106).
- **RED first.** Generator tests over source snippets: one entry per `(model, foreign event)` with the right key column;
  two interfaces with explicit `Targets` get two keys; each analyzer error fires on its shape and not on the valid one;
  a perspective with no fan-out emits exactly what it emits today (snapshot of the generated output).
- **Docs.** New section "Fan-out from another stream" in `fundamentals/perspectives/multi-stream.md`; a diagnostics page
  per new id under `operations/diagnostics/` and its row in `error-reference.md`.

### F2. Live routing to the collective sink

- **Changes.** The association startup step writes a fan-out association for each fan-out type;
  `CollectiveRoutingAssociations.AssociationsHash` includes fan-out types, so adding or removing a fan-out changes the
  perspective's associations hash and the existing rebuild-on-change path rebuilds it (rows written before the fan-out
  existed pick up earlier renames from history, with no backfill).
- **RED first (SQL, real Postgres).** A stored event of a fan-out type gets exactly one `__collective__` row on both the
  outbox and inbox paths; two perspectives fanning out the same type still get one row; an ordinary event gets none and
  `StorePass_AnOrdinaryEvent_RunsNoCollectiveRoutingAsync` stays green; the rewind floor, the straggler reconcile and
  the reap floor treat the row as they treat a collective's. Associations hash test: adding a fan-out changes it.
- **Docs.** `fundamentals/messaging/collective-events.md` ("Persistence, routing, and dispatch": fan-out rows).

### F3. Live set-based apply on both drivers

- **Changes.** The sink recognizes a fan-out envelope and dispatches it through `CollectiveDispatcher` with a
  `FanOutScopeResolver` whose filter is `key = event stream id AND tenant = event tenant`. Keyset batches, lock,
  progress and reach as for collectives. Mark and clear are plain setters; a fan-out spec has no purge.
- **RED first (integration, both drivers).** The issue's example: `CustomerRenamed` on c-1 updates o-1 and o-2 and leaves
  o-3 (c-2) alone; a row of another tenant with the same `CustomerId` is untouched; a redelivered event applies once;
  `CustomerDeleted` marks the rows and removes none; a batch interrupted after one keyset batch resumes after it (#1969);
  a poison fan-out `Apply` reports a failure and does not stop the host.
- **Docs.** The multi-stream section's live half, with a mermaid diagram of the route; `operations/workers/perspective-worker.md`.

### F4. Rebuild and rewind fold the targeted foreign events

- **Changes.** A new default member on `ICollectiveReplayApplier` (for example `InterleaveFanOutAsync`) implemented in
  `CollectiveReplayApplier`: during a rebuild or rewind fold, when the row's key takes a value it has not held, read that
  stream's fan-out events after the current position and merge them into the remainder in `ReplayEventOrder`. The
  in-memory evaluator applies one only while the row's key equals its stream id. Foreign streams archived whole are read
  through the archive as `ArchivedCollectiveStreams` does for collective streams. Snapshot boundaries follow #1956 and
  #2449: a snapshot taken at an event does not hold fan-out events committed after it.
- **RED first.** Live then rebuild give the same rows for: a rename committed after `OrderPlaced`; a rename committed
  before it (it applies to no row, as live); an order reassigned from c-1 to c-2 (only c-2's later renames apply after
  the move); two renames of one customer (last wins); a received rename stored out of producer order (follows
  `ReplayEventOrder`); a rewind from a snapshot does not fold a fan-out event the snapshot already holds; a rebuild with
  the customer stream archived whole.
- **Docs.** `fundamentals/perspectives/rebuild.md` and `rewind.md` (what a fan-out row replays);
  `extending/internals/event-ordering-invariant.md`.

### F5. Live comes out the same as replay when events interleave

- **Changes.** Two rules, so the live row equals the commit-order fold:
  1. **A fan-out applied after a newer own event rewinds the row.** The sink's `UPDATE` returns the ids it touched; a
     touched row whose cursor's last applied event has a higher commit sequence than the fan-out event is flagged for
     rewind with that event as trigger.
  2. **A row folding its own event merges what the sink already applied after it.** When the runner folds a row's events
     live, it merges the fan-out events of the row's keys that the sink has applied and that were committed after the
     batch's first event. To close the window where the sink's `UPDATE` misses a row that commits a moment later and the
     row's read misses the sink's commit, the sink takes `CollectiveApplyLockKey` exclusively per `(table, key)` and the
     runner takes it shared for the keys it folds, emitted only for models with a fan-out.
- **RED first.** Each interleaving held at named points: the sink applies the rename before the row's `OrderPlaced` is
  folded (row created after the `UPDATE`); the row folds a later own event before the sink applies an older rename; both
  commit concurrently. Each test asserts the live row equals a rebuild of it. A model without a fan-out issues no lock
  statement (captured SQL).
- **Docs.** The multi-stream section's ordering half; a design-choice entry (below).

### F6. Sample and design-choice locks

- **Changes.** A sample perspective in the samples tree with the issue's Orders and Customers example; one test per
  decided choice that fails if it flips: a fan-out spec cannot purge; `Targets` accepts a key and nothing else; replay
  merges by commit order, not by event id; received fan-out events follow `ReplayEventOrder`.
- **RED first.** The lock tests above, each written to fail against a deliberately wrong variant.
- **Docs.** A design-choices entry per decision, each linking its test and the decision comment (the rule that every
  design choice has a docs page with the reason and a test, linked both ways).

## Rolling deploy

- **Fleet.** F2's associations and F1's index are application schema changes the migration ledger records, so the release
  that first carries them runs the standby handshake (`operations/startup/rolling-upgrades`): older instances stand by
  before the change commits and shut down after it, so no older sink claims a fan-out row. F2 still adds a mixed-fleet
  test: an older sink that meets a sink row for an event that is not an `ICollectiveEvent` must not lose it silently.
  Today the sink completes rows it finds nothing to dispatch for; if that would drop a fan-out on an instance that
  overrides the stand-down, the row has to stay pending instead.
- **Rollback.** A rollback across the release stands down at Assess (the rollback gate, #2022). Rows the fan-out updated
  stay updated, and an older build reads them, since the columns are ordinary physical fields.
- **Adding a fan-out to a deployed perspective** changes its associations hash, which rebuilds it from history (F2).
  For a large table that is a planned rebuild; the docs say so.

## Docs pages (docs repo, `src/assets/docs/v1.0.0/`)

- `fundamentals/perspectives/multi-stream.md`: the fan-out section (F1, F3, F4, F5)
- `fundamentals/messaging/collective-events.md`: how a fan-out relates to a collective, and its routing (F2)
- `fundamentals/perspectives/rebuild.md`, `rewind.md`: fan-out rows on replay (F4)
- `extending/internals/event-ordering-invariant.md`: the commit-order merge (F4, F5)
- `operations/workers/perspective-worker.md`: the sink dispatches fan-out events (F3)
- `operations/diagnostics/whizNNN.md` per new id, and `error-reference.md` (F1)
- A design-choices entry per decision (F6)

## Open risks

- **The routing trick (F2) rests on every `__collective__` special case being correct for a non-collective event.**
  F2's tests decide it; the fallback costs a hot-path migration.
- **Key discovery assumes the key changes only through the row's own events.** F1 makes that an analyzer error. A key
  set by a collective `[CollectiveApplyFor]` handler on the same model would break it too; F1 also refuses that.
- **"Fresh" depends on the creating event.** A rename committed before `OrderPlaced` applies to no row, live or on
  replay, so the new row holds whatever name `OrderPlaced` carries. The framework guarantees live equals replay, not
  that the producer copied the latest name. The docs say so.
- **Fan-out width.** A customer with 100,000 orders is one keyset-batched `UPDATE`, as a wide collective is. Rule 1 of
  F5 then reads the cursors of every touched row; that read is batched with the `UPDATE` batches and measured in F5.
- **The per-key lock is the collective-versus-standard coordination that `plans/collective-5b-standard-apply-shared-lock-deferred.md`
  deferred for collectives.** It is justified here because live correctness is the feature, and it is scoped to
  fan-out models only. If measurement shows contention, the alternative is rule 1 alone plus a rewind of rows the
  `UPDATE` missed, found by a key lookup after the row commits.
- **#1627 is still open.** F4 uses `ReplayEventOrder` as it stands; whatever #1627 settles on for received streams applies
  to fan-out events through the same function, with no fan-out-specific rule.
- **Tenancy.** A fan-out targets rows of the event's tenant only. A system-scoped foreign event (no tenant) targets
  nothing in 1.0; a real case for cross-tenant fan-out would be a new question.
