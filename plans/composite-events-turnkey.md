# Plan: composite events as dispatchable, fan-out-at-dispatch messages (turnkey)

## Goal

Make `ICompositeEvent` a **turnkey, architecturally-correct** Whizbang feature, then adopt it in a consumer
bulk import (one composite per job). A composite is "N events batched for the wire." The correct shape:

> A composite is an **ordinary message** everywhere except **one seam — the fan-out** — and that seam
> sits **inside the durable inbox/dispatch/retry/DLQ envelope**, not outside it at the transport edge.

This replaces the original "wire-only, expand at the transport consumer" design (which orphaned the
composite from durability/retry/DLQ and forced pervasive special-casing). The serialization +
`CompositeEventBase` work already done stays valid; what moves is **where** fan-out happens.

## The three message roles (one consistent lifecycle)

1. **Composite** — dispatchable + hookable, but **never written to the event store** (only children
   are). Lives transiently as an **inbox row**: received → pre-fanout receptors → fan out → deleted.
2. **Children (inner events)** — ordinary received events. Produced by fan-out as **inbox rows**,
   processed normally (event store + receptors + perspectives). **Never outboxed** (see invariant).
3. Everything downstream of fan-out sees only children — no composite awareness.

### Dispatch lifecycle (at the dispatch seam, inside retry/DLQ)

```
claim composite inbox row
  → PRE-FANOUT:  dispatch composite to IReceptor<TComposite> (same tx; optional)
  → FANOUT:      expand InnerEvents → N child inbox rows   (automatic by default)
  → COMMIT:      pre-fanout effects + child rows + delete-composite  (atomic)
  → POST-FANOUT: each child inbox row dispatches normally to IReceptor<TChild>
```

- **Failure** at pre-fanout or fanout → the composite is *an inbox row that failed* → normal retry →
  DLQ via the existing `IDeadLetterStore.MoveAsync(wh_inbox, …)`. **Phase 3/DLQ disappears.**
- **Child failures**: `Independent` (default — each child its own inbox row + retry/DLQ; one bad
  child doesn't sink the batch) or `Atomic` (children expand-and-process in one tx, all-or-nothing).

## Hooks (no new concepts — just receptor typing)

- **Pre-fanout** = `IReceptor<TComposite>` (the composite is a real message at the surface): validate
  the batch, stamp batch metadata, emit a durable `BatchReceivedEvent`, or drive fan-out.
- **Post-fanout** = `IReceptor<TChildEvent>` — the normal per-event receptors. No special-casing.
- The only registry change: composite types become **dispatchable** (today's transport-expansion
  model treats them as not-dispatchable). That seam is what makes "a receptor can listen for the
  composite" work.

## Fan-out control (automatic default, layered override)

- **Default**: zero-config — any `ICompositeEvent` auto-fans-out, children processed `Independent`.
- **Declarative** (common) — on the composite type / `CompositeEventBase`:
  - `FanoutMode`: `Auto` (default) | `Manual` (a receptor drives it).
  - `Atomicity`: `Independent` (default) | `Atomic`.
- **Imperative** — a pre-fanout receptor returns a `FanoutDirective`:
  `Proceed` (default) | `Skip` (handle the composite without fanning out — a pure control signal) |
  `ReplaceWith(children)` (filter / transform / re-key children before they're created).

## The no-rebroadcast invariant (children never outbox) — enforced, not emergent

> One composite on the wire; children are received-events confined to the
> inbox → event-store → local-processing path.

Correct **by construction** — the outbox is producer-only (`PublishAsync`); fan-out writes children
to the **inbox/received** path, never `PublishAsync`. Children inherit the composite's routing, and
the composite was already delivered to every subscriber of its topic, so a child never needs its own
broadcast (a child needing different routing shouldn't be in a composite — same rule as per-inner
StreamIds). Defended in depth:

1. **Hop-based suppression (exists)** — children inherit the composite's `Hops`; existing owned-echo /
   re-broadcast suppression treats them as "received from upstream" and won't re-publish them.
2. **Explicit flag used as a GUARD (new, required)** — fan-out stamps children with an `EventFlags`
   marker (`NoRebroadcast` / `LocalOnly`). The **outbox-enqueue boundary hard-checks the flag and
   drops** flagged children. This turns "children don't outbox" into an *enforced invariant*: even a
   future receptor or code path that explicitly publishes a fan-out child is dropped at the outbox
   gate. Scope: the marker is on the **children themselves**, not on genuinely-new downstream events a
   child's receptor produces (those outbox normally).

## Engineering constitution (every phase)

- **L1/L4 RED→GREEN TDD** — a failing test first; minimum code to green; refactor under green.
- **L2 100% diff coverage** (line + branch) on changed lines; **L7** mutation-aware (no coverage-only
  tests).
- **L19 AOT-strict, no reflection** — all wire/registration/dispatch-routing work goes through the
  source generator + `JsonContextRegistry`; zero runtime reflection.
- **L30–L32 docs↔code↔tests** — ship/refresh `docs/fundamentals/messaging/composite-events.md`; every
  new public type carries `<tests>`/`<docs>` frontmatter; close the graph (MCP-indexed).

---

## Status

### DONE (committed on `feature/composite-events-turnkey`; still valid under the new spine)
- **Serialization (P1)** — generator registers `ICompositeEvent` as an `IMessage` wire type (not
  `IEvent`, so never persisted); cycle-safe nested-`IMessage` polymorphism via a resolver-supplied,
  skip-unresolvable base typeinfo; edge coverage (composite-in-composite, polymorphic collections,
  mixed/empty). Commits `2e72c74b`, `ee9b59e5`, `ede61b6e`. Core 8126/0, generator 1219/0.
- **`CompositeEventBase` helper (P2)** — `[StreamId]`, `List<IMessage> Inner`, `[JsonIgnore]` computed
  `InnerEvents`, init `MaxInnerEventsAllowed`, `EnsureWithinCap()`. Surfaced + fixed two real generator
  bugs (abstract-type instantiation; `[JsonIgnore]` not honored). Commit `91bc9d85`.
  - **Will gain** `FanoutMode` + `Atomicity` in Phase C.

### REMAINING — the new spine (each phase RED→GREEN, 100% cov, AOT, docs)

- [ ] **Phase A — composite as a dispatchable inbox message; fan-out moves to dispatch.**
  - Make composite types **dispatchable** (receptor registry + generator includes them).
  - Transport consumer: **stop expanding** — store the composite as an ordinary inbox row. Remove the
    `EventFlags.Composite` branch + `_tryExpandCompositeToInboxMessages` from `TransportConsumerWorker`.
  - Dispatch seam (work-batch processor): recognize `ICompositeEvent`, **auto-fan-out** into child
    inbox rows (reusing `CompositeEventExpander` mechanics), atomic insert + delete-composite in the
    dispatch tx.
  - RED→GREEN: composite over (test) transport → becomes inbox row → fanned out at dispatch → children
    processed; fan-out failure → inbox retry → **DLQ via existing `MoveAsync(wh_inbox)`** (this is
    Phase 3, now free — assert a `wh_dead_letters` row, no new infra).
- [ ] **Phase B — pre-fanout hook + post-fanout children.**
  - `IReceptor<TComposite>` fires pre-fanout, in the dispatch tx, before any child exists.
  - Children dispatch normally post-fanout. Lock the ordering (pre → fanout → commit → children) and
    that pre-fanout side-effects + children commit atomically.
- [ ] **Phase C — fan-out control.**
  - Declarative `FanoutMode` (Auto/Manual) + `Atomicity` (Independent/Atomic) on the composite /
    `CompositeEventBase`.
  - Imperative `FanoutDirective` (Proceed/Skip/ReplaceWith) returned by a pre-fanout receptor.
  - RED→GREEN per mode/directive; `Atomic` rolls back all children on any failure; `Independent`
    isolates per child.
- [ ] **Phase D — no-rebroadcast flag GUARD.**
  - New `EventFlags.NoRebroadcast` (or `LocalOnly`); fan-out stamps every child.
  - **Outbox-enqueue boundary checks the flag and drops** flagged children (the guard).
  - RED: a flagged child routed to the outbox path is enqueued (guard absent) → GREEN: dropped.
  - Plus a test that hop-based suppression covers the same case (defense-in-depth).
- [ ] **Phase E — docs** — `docs/fundamentals/messaging/composite-events.md`: the lifecycle, hooks,
  control surface, no-rebroadcast invariant; link to `CompositeEventBase`, the dispatch seam, and the
  tests. Refresh `ICompositeEvent` XML docs to describe dispatch-time fan-out (not transport-edge).
- [ ] **Release** — Whizbang alpha (GitVersion `release/*` → PR → develop → nuget); bump a consumer
  `Directory.Packages.props`.
- [ ] **Track 2 — a consumer per-job composite.**
  - `OrderBulkImportComposite : CompositeEventBase` with `Atomicity = Atomic` (a job's field events
    are one unit), `FanoutMode = Auto`. Optional pre-fanout receptor for the cap guard / per-job
    metadata.
  - Rewire `BulkImportSagaHandlers.PublishOrderEventsAsync`: pre-mint `jobStreamId`, publish ONE
    composite (replacing `PublishAsync(init)` + `PublishManyAsync(rest)`). Saga lifecycle unchanged.
  - RED→GREEN expansion-parity integration test: same N event-store rows on `jobStreamId` as today,
    per-job atomic rollback on failure, and the no-rebroadcast invariant holds.
  - Measure the 350-import win on production.

## Risks / open questions

- **Dispatch-seam reuse**: prefer reusing `CompositeEventExpander` at the dispatch step over a parallel
  implementation. Confirm the work-batch processor is the right host (it owns claim/retry/DLQ).
- **`Atomic` transaction size**: a large atomic composite is one big transaction — bounded by
  `MaxInnerEventsAllowed` + the cap guard; `Independent` is the default for exactly this reason.
- **Never-in-event-store invariant** stays: composites are inbox-transient only; the producer append
  path already skips them (`IsEvent == false`). Keep the belt-and-suspenders Composite-flag guard on
  Phase 4.5A append if a consumer ever marks a composite `IEvent`.
- **Routing**: children inherit the composite's stream/routing; cross-domain children must not be
  composited (same rule as per-inner StreamIds) — document this constraint.

## Commit strategy

One commit per phase, `feat(messaging): <phase>`; tests + docs in the same commit (L30–L32). End
messages with the required `Co-Authored-By` trailer.
