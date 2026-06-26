# Plan: turnkey composite events (Whizbang) + per-job composite in bulk import (a consumer)

## Goal

Make `ICompositeEvent` a **turnkey** Whizbang feature — authoring a composite "just works"
(serialization, registration, AOT, helpers, observable failures) — then adopt it in a consumer bulk
import: **one composite per job** (≈350 composites for a 350-import), each bundling that job's
field events on the job's own stream. The per-item saga lifecycle is untouched; the composite only
replaces the per-job domain-event publishing.

Receiving side is **transparent**: receptors and perspectives see ordinary per-stream inner events
and never know a composite existed. Inner events inherit the composite's StreamId + identity context
(tenant/scope/correlation) and get fresh MessageIds; the composite itself is never persisted (replay
sees only inner events).

## Engineering constitution (applies to every phase)

- **L1/L4 TDD red→green**: a failing test first, minimum code to green. No production line without a
  test that was RED before it.
- **L2 100% diff coverage** (line + branch) on changed lines; **L7** mutation-aware (don't write
  coverage-only tests).
- **L30–L32 docs**: ship/refresh `docs/fundamentals/messaging/composite-events.md`; close the
  docs↔code↔tests graph (doc front-matter `related_files`/`related_tests`; MCP-indexed).
- **L19 AOT-strict, no reflection**: all wire/registration work goes through the source generator and
  `JsonContextRegistry`; zero runtime reflection. This is the AOT-critical surface.

## Background (verified against `release/v0.758.1-alpha.1`)

- `ICompositeEvent : IMessage` (NOT `IEvent`) — `src/Whizbang.Core/Messaging/ICompositeEvent.cs`.
  `InnerEvents` (ordered, lazy-ok), `MaxInnerEventsAllowed` (default 10k).
- **Producer**: `Dispatcher` stamps `EventFlags.Composite` on the single outbox row
  (`Dispatcher.cs:3738`, `:4860`); because `IsEvent == false`, Phase 4.5A skips the composite when
  copying outbox→event store (`Migrations/029_ProcessWorkBatch.sql:437-440`) — never persisted.
- **Receiver**: `TransportConsumerWorker.cs:461` branches on `EventFlags.Composite`,
  `CompositeEventExpander.Expand` (`src/Whizbang.Core/Messaging/CompositeEventExpander.cs:74`) yields
  inner envelopes sharing the composite's `Hops` by reference; all inner inbox rows insert in one
  `store_inbox_messages` transaction. Inner StreamId = composite StreamId via shared hop `AggregateId`
  (`TransportConsumerWorker.cs:1107`). Inner `[GenerateStreamId]`/`[StreamId]` are ignored at receive.
- **Gaps that make it NOT turnkey today**:
  1. **Serialization/registration**: the JSON source-gen discovers types by `IsCommand || IsEvent ||
     [WhizbangSerializable] || perspective-model` and emits `RegisterDerivedType<IMessage,…>` ONLY for
     `IsEvent || IsCommand` (`MessageJsonContextGenerator.cs:575-576`). A type implementing only
     `ICompositeEvent` is undiscovered/unregistered → `MessageEnvelope<ICompositeEvent>` may not
     round-trip. Workarounds are both flawed (`[WhizbangSerializable]` skips the `IMessage`
     registration; `IEvent` flips `IsEvent=true` → producer would persist the composite).
  2. **No authoring helper** — consumers hand-roll `InnerEvents`, StreamId stamping, cap checks.
  3. **Failure is silent** — receiver expansion failure logs-and-drops (ACKs); no DLQ, no signal
     (`TransportConsumerWorker.cs:519-522`).

## Decisions (locked; override if needed)

- **Helper = base class** `CompositeEventBase` (nominal type → clean source-gen discovery + topic
  routing), not a factory.
- **DLQ failure-surfacing** (Track 1 phase 3) is planned but **does NOT block** the a consumer prototype.
  The prototype uses optimistic saga completion + a producer-side count-vs-cap guard (a malformed
  composite fails synchronously at publish via the existing try/catch). DLQ hardening lands alongside.

---

## Track 1 — Whizbang turnkey support (ships first)

### Phase 1 — serialization/registration "just works" (task #42)
- **RED**: cross-service round-trip test — a type implementing only `ICompositeEvent` serialized into
  `MessageEnvelope`, sent through the (test) transport, fails to deserialize/expand because it isn't a
  registered `IMessage` derived type. (Unit-level: assert the generated initializer contains no
  `RegisterDerivedType<IMessage, TheComposite>` today.)
- **GREEN**: in `MessageJsonContextGenerator` — discover `ICompositeEvent` implementers; emit their
  `JsonTypeInfo`; emit `RegisterDerivedType<IMessage, T>` for them; do **not** set `IsEvent`/emit
  `RegisterDerivedType<IEvent,…>`. Add a `Composite`-flag guard to Phase 4.5A append SQL as a
  belt-and-suspenders "never persisted" safety net (defense even if a future composite is also IEvent).
- **Coverage/AOT**: generator snapshot tests + a runtime round-trip test; no reflection.

### Phase 2 — authoring helper `CompositeEventBase` (task #43)
- **RED**: test that `CompositeEventBase` stamps the shared StreamId, yields inner events in
  insertion order, and throws/synchronously-fails when inner count > `MaxInnerEventsAllowed`.
- **GREEN**: `abstract class CompositeEventBase : ICompositeEvent` with `[StreamId] Guid StreamId`,
  ctor `(Guid streamId, IReadOnlyList<IMessage> inner)`, `InnerEvents` passthrough, overridable cap,
  and a producer-side `ValidateInnerCount()` the dispatcher calls (or the base enforces lazily).
- Docs example uses this base.

### Phase 3 — expansion-failure DLQ routing (task #44, non-blocking)
- **RED**: expansion failure (over-cap / malformed inner) currently ACKs and drops — test asserts no
  DLQ row. 
- **GREEN**: route `CompositeInnerEventLimitExceeded` / `CompositeExpansionFailure` to the real
  failure channel/DLQ (mirror the body-claim rehydrate DLQ path at `TransportConsumerWorker.cs:654`).

### Release
- New Whizbang alpha (GitVersion on a `release/*` → PR to develop), then bump a consumer
  `Directory.Packages.props` (same loop as the `IVersionedApplyTarget` fix).

---

## Track 2 — a consumer per-job composite (after Track 1 + a consumer bump) (task #45)

1. `OrderBulkImportComposite : CompositeEventBase` — inner events init-first then field events
   (optionally fold the interim `OrderBulkImportItemCompletedEvent` marker; both share the job
   stream).
2. Rewire `BulkImportSagaHandlers.PublishOrderEventsAsync`: pre-mint `jobStreamId`
   (`TrackedGuid.NewMedo()`), set it on the composite + all inner events, replace
   `PublishAsync(init)` + `PublishManyAsync(rest)` with one `PublishAsync(composite)`. Saga
   `UpdateItemAsync`/`FailItemAsync` unchanged.
3. **RED→GREEN expansion-parity integration test**: a composite-per-job lands the **same N
   event-store rows on `jobStreamId`** (types + order) as today's individual-publish path
   (replay-identical), plus a cap test and a per-job-failure test.
4. **Measure**: 350-import wall-clock + dispatch/outbox/transport counts, composite vs current, on
   production.

## Risks / open questions

- Phase 1 is the load-bearing spike: confirm the generator change makes the envelope round-trip
  end-to-end (not just emits the line). If `JsonContextRegistry` needs more than the derived-type
  registration, scope grows.
- "Batched event-store append, all-or-nothing" is **partial** — receive-time atomicity is the inbox
  insert; event-store landing is per-row idempotent self-healing. Parity test asserts the end state,
  not a single transaction.
- Failure visibility: until Phase 3 lands, a downstream expansion failure is silent (same risk class
  as today's outbox-processing failures); the producer-side cap guard catches the common case.

## Status

- [x] T1.P1 serialization/registration (RED→GREEN) — registration + nested-polymorphism both done
  - [x] Generator discovers `ICompositeEvent` → emits `JsonTypeInfo` + `RegisterDerivedType<IMessage,…>`,
        NOT as IEvent. RED→GREEN (`Generator_WithCompositeEvent_RegistersAsIMessage_NotAsEventAsync`);
        full generator suite 1219/0.
  - [x] End-to-end round-trip test written (`MessageEnvelope_CompositePayload_RoundTripsWithInnerEventsIntactAsync`)
        — **found a real gap (RED, currently `[Skip]`):** the composite serializes, but its nested
        `IMessage` list (`InnerEvents`) fails to deserialize — STJ: *"Deserialization of interface or
        abstract types is not supported. Type 'IMessage'. Path: $.Payload.Items[0]"*.
  - [x] **T1.P1b — nested IMessage polymorphism (cycle-safe fix). DONE.** `CreateCombinedOptions` now
        prepends a `_polymorphicBaseTypeInfoResolver` that supplies a polymorphic typeinfo for the
        registered base interfaces (`IMessage`/`IEvent`/`ICommand`) via explicit generic dispatch
        (AOT-safe). Cycle-safety comes from a **lazy** builder (`_createPolymorphicTypeInfoLazy`) that
        adds `JsonDerivedType` entries WITHOUT forcing resolution (`MakeReadOnly`/`Properties`) — so
        building the base typeinfo never re-enters resolution for a nested same-base member; STJ
        resolves derived types lazily against the cached base typeinfo. Round-trip test now GREEN;
        full `Whizbang.Core` suite **8115/0**, no regressions.
  - [ ] (belt-and-suspenders, deferred/low-pri) `Composite`-flag guard on producer Phase 4.5A append —
        moot while the generator keeps composites IMessage-only (`IsEvent=false` already skips them);
        only needed if a consumer hand-rolls a composite that also implements `IEvent`.
- [ ] T1.P2 `CompositeEventBase` helper
- [ ] T1.P3 DLQ failure routing
- [ ] T1 release + a consumer bump
- [ ] T2 a consumer per-job composite + parity test + measure
