# Deferred: §5b — standard-apply shared advisory lock (collective ↔ standard coordination)

**Status**: DEFERRED (decided 2026-07-01). §5a shipped; §5b intentionally not built.
**Context**: Whizbang 0.795 collective-event apply hardening (the production spiral fix). See the
0.795 plan (§5 "Per-(model, scope) reader/writer serialization"). This is the **standard-side** half of
that section.
**Owner decision**: deferred by the maintainer after weighing cost vs. risk vs. ROI (thread below).

---

## TL;DR for a future session deciding whether to pick this up

Collective-vs-**collective** cross-pod serialization is **already done** (§5a, shipped). §5b would add
collective-vs-**standard** coordination: the ordinary single-row perspective apply would take a *shared*
advisory lock so it waits briefly while a collective batch sweeps its (model, scope). It is **not a
correctness fix** — Postgres row locks already prevent corruption and collective setters are constant/
idempotent — it is an *ordering / throughput-coordination* guarantee. It was deferred because the only way
to build it is an invasive change to the **generated standard-apply hot path** (every perspective in every
service) + the drain path, and the payoff is belt-and-suspenders. Revisit if you ever observe a concrete
ordering anomaly between a collective apply and concurrent standard writes that event-replay determinism
does *not* resolve.

## What DID ship (§5a — for contrast, so you don't re-derive it)

- **`EFCoreCollectiveAdapter`** takes an **exclusive** `pg_advisory_xact_lock(hash(table, scopeKey))` per
  keyset batch (`CollectiveApplyLockKey.Compute`, FNV-1a, process-stable). Because Postgres advisory locks
  are **database-global** (visible to every session on the DB regardless of pod/connection-pool), this
  serializes collective applies to the same (table, scope) **across all instances** — pod B's collective
  apply blocks on the same key pod A holds. This is what broke the production convoy.
- Opt-out: `CollectiveApplyOptions.SerializeApplies = false` (default true). No per-handler opt-out —
  exclusive serialization is not optional (D4 safety).
- Granularity note: the lock is **per batch**, not per whole apply — so two collective applies to the same
  scope *interleave at batch granularity* rather than one fully draining before the other starts. Deliberate
  (avoids a long apply blocking another for its whole duration; idempotent setters make interleaving safe).
  If strict whole-apply serialization is ever wanted, that is a *different* lock scope (hold across the loop),
  not §5b.

## What §5b WOULD be (and did NOT ship)

Two layers, per the plan:

1. **Postgres shared advisory lock on the standard per-row apply.** The generated `*Runner` standard-apply
   path (and `_runDrainModePerspectiveAsync`) would take `pg_advisory_xact_lock_shared(hash(table, scope))`
   in the same transaction as the row write, for models that have a `[CollectiveApplyFor]` handler. Shared
   locks are compatible with each other (concurrent standard applies don't serialize among themselves), but
   the collective apply's **exclusive** lock (§5a) would then block *all* standard writes to that (model,
   scope) for its brief batched duration, and vice-versa.
2. **In-pod semaphore keyed on (TModel, scope)** as a fast path so same-pod collective-vs-standard doesn't
   even round-trip the DB lock. Must cover **both** the channel path and the drain path (the drain path
   currently bypasses the affinity gate).

## Why it was deferred — reservations

1. **The real cost is codegen risk, not the DB call.** (The maintainer correctly pushed back that "one more
   DB call" is cheap — the standard apply is already inside a transaction doing a write, a
   `pg_advisory_xact_lock_shared` is one extra lightweight statement, and shared locks don't contend among
   themselves.) The actual risk is that §5b requires injecting a lock preamble into the **generated
   standard-apply path — the hottest, most-shared code in the framework** (every perspective, every service)
   — plus the drain path, plus an in-pod semaphore. Getting the scope-key derivation **byte-identical** to
   §5a's (`CollectiveApplyLockKey.Compute(table, scopeKey)` with the *same* `scopeKey =
   evt.Scope.ScopeKind + ":" + evt.Scope.ToString()` derivation) is essential — if it drifts, the shared and
   exclusive locks hash to different keys and §5b becomes a silent no-op that *looks* like it works. That is a
   testing/verification burden, not a runtime-cost burden.
2. **Marginal correctness ROI.** Row locks already serialize two writes to the *same* row (no corruption),
   and collective setters are constant/idempotent, so interleaving a collective batch with standard applies
   is already safe. Eventual consistency of the projection comes from **event-replay determinism** (the log
   order fixes the final state on rebuild), not from live lock ordering. So §5b buys smoother *live* ordering,
   not a new correctness guarantee.

## Pros / cons for the future vetting session

**Pros of building it**
- Defense-in-depth: a firing collective apply cleanly quiesces standard writes to its (model, scope) for its
  brief window, so live projection state transitions through fewer transient interleavings.
- Cheap at runtime (one shared-lock statement per standard apply on collective-enabled models; shared locks
  don't contend; only blocks under the brief exclusive collective lock).
- Completes the plan's §5 as originally scoped ("Both (in-pod + advisory)").

**Cons / risks**
- Touches the generated standard-apply hot path + drain path for **all** collective-enabled models — broad
  blast radius, high regression surface, needs careful codegen tests.
- Silent-no-op failure mode if the scope-key derivation drifts from §5a — needs an explicit test that a
  collective exclusive lock actually *blocks* a concurrent standard apply to the same (model, scope) and does
  *not* block a different scope.
- Adds lock-manager traffic on the hottest write path (negligible per-op, but non-zero at high throughput).
- ROI is belt-and-suspenders given §5a + row locks + idempotent setters + replay determinism already hold.

## What implementing it would entail (so a future session can scope it)

- **Generator**: the standard-apply runner (`PerspectiveRunnerRegistryGenerator`) must know which models have
  `[CollectiveApplyFor]` handlers (the `CollectiveApplyDiscoveryGenerator` already discovers this) and emit a
  `pg_advisory_xact_lock_shared(key)` preamble **only** for those models, in the same transaction as the row
  write. Non-collective perspectives must pay **zero** cost.
- **Shared key**: reuse `CollectiveApplyLockKey.Compute(table, scopeKey)` and the exact `scopeKey` derivation
  from `CollectiveEventApplier` so shared/exclusive correspond. Consider extracting the scopeKey derivation to
  one shared helper so it cannot drift.
- **Drain path**: `_runDrainModePerspectiveAsync` currently skips the affinity gate — the shared-lock preamble
  must cover it too.
- **In-pod semaphore**: a `(TModel, scope)`-keyed semaphore in the dispatcher/adapter (collective side) and
  the runner (standard side), covering channel + drain.
- **Tests (the point of the exercise)**: an integration test proving a collective exclusive lock **blocks** a
  concurrent same-(model,scope) standard apply while a **different** scope proceeds concurrently; plus a
  generator test that the shared-lock preamble is emitted only for collective-enabled models.

## How to know it's worth doing

Pick it up if any of these show up:
- A reproducible ordering anomaly where a collective apply and concurrent standard writes to the same scope
  leave *live* projection state in a shape that replay/rebuild would not (i.e. not self-healing).
- Operational need for a collective apply to "quiesce" its scope's standard writes for its window (e.g. a
  consistency-sensitive bulk operation whose intermediate visibility matters).
- The in-pod semaphore alone (cheaper, no codegen-hot-path change) turns out to be enough — in which case
  build *only* that layer and skip the DB shared lock.

## Pointers

- §5a implementation: `src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveAdapter.cs`,
  `src/Whizbang.Data.Postgres/Collective/CollectiveApplyLockKey.cs`,
  `src/Whizbang.Data.EFCore.Postgres/Collective/CollectiveEventApplier.cs` (scopeKey derivation).
- Discovery of collective-enabled models: `src/Whizbang.Generators/CollectiveApplyDiscoveryGenerator.cs`.
- Standard-apply runner: `src/Whizbang.Generators/PerspectiveRunnerRegistryGenerator.cs`,
  `_runDrainModePerspectiveAsync` (drain path).
