# Audit Event Perspective Priority

## Context

The PerspectiveWorker drains `wh_perspective_events` via `process_work_batch`'s Phase 7 using a two-tier budget (`v_tier1_max = 70%` for small streams, remainder for large streams). Today, **all perspectives compete for the same event budget** — the tier split is based on per-stream volume, not on the semantic importance of the perspective.

In live production observations (2026-04-23), a single bulk job import produces:

- ~8,400 events for `Order+Projection` (the aggregate projection that drives the UI read model)
- ~2,200 events for `OrderCompetency+Projection`
- ~970 events for `OrderOrderLines+Projection`
- ~965 events for `OrderSkill+Projection`
- …plus N audit-style perspectives that log events to an audit store

The user-visible UX (UI "Validating..." → "Completed" transition) depends on the **UI-driving projections** reaching steady state. A noisy audit-perspective that handles every event can starve the UI projections — audit writes and UI writes both consume the same 1,000-event-per-cycle budget, so a burst produces ~50/50 progress across both categories even though the UI is a thousand times more latency-sensitive.

**Goal:** give audit-style perspectives a lower priority than UI-driving perspectives so the hot event budget goes to the perspectives users are waiting on, and audit catches up once the user-facing work settles.

## Requirements

1. **Opt-in priority tier** — default perspectives keep current behavior; audit perspectives opt into the lower tier via an attribute or registration API.
2. **SQL-level budget split, not just C# sorting** — prioritization has to happen in `process_work_batch` (migration 029) so the worker only claims low-priority work once high-priority is drained. C#-side sorting is useless because the SQL already caps the returned set via `v_max_work_items`.
3. **AOT-compatible** — no runtime reflection, no attribute discovery via `Type.GetCustomAttributes`. Source-generated metadata only.
4. **Backward-compatible** — existing perspectives without the attribute behave exactly as today (default tier, existing budget split).
5. **Starvation guard** — audit perspectives must still get SOME events per cycle to avoid pathological growth of the audit backlog. Suggested: reserve a small floor (e.g., 5–10% of the cycle budget) for audit.

## Design sketch (not final — needs its own investigation phase)

### Schema change
Add a `priority_tier` column to `wh_perspective_events` (SMALLINT, default 0):
- `0` = Default (UI-driving projections) — current behavior
- `1` = Audit (low priority)
- Space reserved for additional tiers later (0..5)

`priority_tier` gets set at Phase 4.6 (auto-create perspective events in migration 029) based on the `wh_message_associations` row — the association registry already maps `message_type → target_name`, and we add a `priority_tier` column there too. The source generator populates `priority_tier` from the attribute at registration time.

### Phase 7 budget split
Replace the existing `tier1_limited` / `tier2_limited` logic in migration 029 (lines 1259–1303) with a **three-way split**:

```sql
WITH
priority_0_small AS (
  SELECT ... FROM eligible_perspective
  WHERE priority_tier = 0 AND stream_pending_count <= v_max_work_items_per_stream
  ORDER BY stream_pending_count, stream_id, perspective_name, event_id
  LIMIT v_priority0_budget -- e.g. 85% of total
),
priority_0_large AS (
  SELECT ... FROM eligible_perspective
  WHERE priority_tier = 0 AND stream_pending_count > v_max_work_items_per_stream
    AND stream_rank <= v_max_work_items_per_stream
  ORDER BY stream_pending_count, stream_id, perspective_name, event_id
  LIMIT (v_priority0_budget - (SELECT COUNT(*) FROM priority_0_small))
),
priority_1_floor AS (
  SELECT ... FROM eligible_perspective
  WHERE priority_tier >= 1
  ORDER BY priority_tier ASC, stream_pending_count, stream_id, perspective_name, event_id
  LIMIT v_priority_floor -- e.g. 10% of total
),
priority_spillover AS (
  SELECT ... FROM eligible_perspective
  WHERE priority_tier >= 1
    AND NOT EXISTS (SELECT 1 FROM priority_1_floor WHERE ...)
  ORDER BY priority_tier ASC, ...
  LIMIT (v_max_work_items - (priority_0_count + priority_1_floor_count))
)
```

Ordering of the union: priority 0 (small → large) → priority 1 floor → priority spillover. The final `distinct_streams` collapse preserves the tier order.

### C# side
Add an attribute:
```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class PerspectivePriorityAttribute(PerspectivePriority priority) : Attribute {
  public PerspectivePriority Priority { get; } = priority;
}

public enum PerspectivePriority : byte {
  Default = 0,
  Audit = 1,
}
```

Users annotate:
```csharp
[Perspective(...)]
[PerspectivePriority(PerspectivePriority.Audit)]
public class JobAuditPerspective : IPerspectiveBase { ... }
```

The existing perspective generator (`src/Whizbang.Generators/`) discovers the attribute at compile time and emits the priority into the source-generated `PerspectiveRegistrationInfo`. Then `ServiceCollectionExtensions` or the generator-emitted registration code writes the association row to the DB with the priority tier populated.

### Options for the budget split
Add two properties to a new `PerspectivePriorityOptions` class (bound from configuration):
- `Priority0BudgetPercent` (default 85) — budget reserved for priority 0 (UI-driving) perspectives
- `PriorityFloorPercent` (default 10) — guaranteed floor for priority 1+ (audit) perspectives per cycle

Spillover (if priority 0 doesn't consume its full budget) flows to audit automatically. The 5% not covered by either variable is flex capacity that defaults to priority 0.

## Critical files for the next session

### To read
- `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql` — lines 1259–1326 (Phase 7 perspective work return)
- `src/Whizbang.Data.Postgres/Migrations/007_CreateMessageAssociationsTable.sql` (or wherever `wh_message_associations` is defined) — add `priority_tier` column
- `src/Whizbang.Data.Postgres/Migrations/009_CreatePerspectiveEventsTable.sql` — add `priority_tier` column
- `src/Whizbang.Generators/PerspectiveGenerator/*.cs` — emit priority from attribute
- `src/Whizbang.Core/Perspectives/PerspectiveRegistrationInfo.cs` — add `Priority` field

### To write
- `src/Whizbang.Core/Perspectives/PerspectivePriorityAttribute.cs` — new attribute
- `src/Whizbang.Core/Perspectives/PerspectivePriorityOptions.cs` — new options class
- Migration: new file (e.g. `043_AddPerspectivePriorityTier.sql`) OR edit 009/029 in place per pre-v1 mutable-migrations convention
- Tests: `tests/Whizbang.Data.Postgres.Tests/` for SQL behavior; `tests/Whizbang.Core.Tests/Perspectives/` for attribute → registration propagation
- Docs: new page `docs/fundamentals/perspectives/priority-tiers.md` in `whizbang-lib.github.io`

## Starting-point questions for the next session

1. **Should `priority_tier` live on `wh_message_associations` (once per message-type × perspective) or on `wh_perspective_events` (duplicated per event)?** The associations table is the right logical home but denormalizing onto `wh_perspective_events` makes the Phase 7 query simpler. Recommendation: both — set on associations at registration, copied to `wh_perspective_events` at Phase 4.6 for query efficiency.
2. **How many tiers?** Start with 2 (Default, Audit). Reserve space for 3–5 total in the column type (SMALLINT has plenty of room).
3. **What's the right default budget split?** 85/10/5 is a guess. Needs a micro-benchmark with a realistic audit+UI workload to tune.
4. **Does the current `v_tier1_max` small-vs-large-stream tiering stack with priority tiering, or does priority replace it?** Recommendation: priority tiers are outer, small/large is inner within each priority.
5. **Migration strategy for existing perspectives?** Pre-v1 convention is to edit migrations in place and recreate DB (`feedback/project_pre_v1_migrations`). Existing perspectives default to tier 0, so no user-facing migration is required.

## Out of scope (future work)

- Per-stream priority overrides (e.g., "this one tenant's audit should be high priority")
- Priority inversion detection (log warnings if a high-priority perspective is stuck while low-priority perspectives are being served)
- Dynamic priority adjustment based on backlog age
- Scheduler-style fairness (weighted round-robin across tiers instead of strict priority)

## Not to be confused with

- **PR #201 PostLifecycle fire-and-forget (commit ccde9309)** — makes PostLifecycle receptors non-blocking at cycle boundary. Orthogonal to this plan.
- **PR #201 Tag fire-and-forget (commit b3cd7d71)** — makes `IMessageTagProcessor` non-blocking. Orthogonal.
- **Bottleneck A/B (PR #201 ASB tuning)** — transport-layer. Orthogonal.

This plan is specifically about giving perspectives a way to opt into a **lower** priority so the hot event budget goes to UI-driving projections first.
