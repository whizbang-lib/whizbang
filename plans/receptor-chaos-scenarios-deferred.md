# Deferred: worker-level chaos scenarios for receptor-firing lock-in

**Status**: primitives in place, scenarios pending worker integration
**Context**: Phase 3b of the receptor-firing exactly-once work

---

## What shipped

Test-only primitives that future chaos tests build on:

- **`IReceptorFiringObserver`** (`src/Whizbang.Core/Messaging/IReceptorFiringObserver.cs`) — before/after callbacks invoked from `ReceptorInvoker._invokeReceptorAsync` around every receptor dispatch. Production resolves null and pays zero cost. Tests register an observer that signals `TaskCompletionSource`s on fire/completion for deterministic waiting. Four unit tests validate the callbacks + the skip-on-guardrail interaction (`tests/Whizbang.Core.Tests/Messaging/ReceptorFiringObserverTests.cs`).
- **`IChaosInjector`** + **`ChaosInjectorInvoker`** + **`ChaosCheckpoints`** (`src/Whizbang.Core/Messaging/IChaosInjector.cs`, `.../ChaosInjectorInvoker.cs`) — named checkpoint hook, gated by `WhizbangOptions.Guardrails.EnableChaosHooks` (default: false). Workers resolve `ChaosInjectorInvoker` once and call `BeforeCheckpointAsync(name, payload, ct)` at named points; production pays zero cost when the flag is off (which it is by default).
- **`ChaosCheckpoints`** — stable constants for the nine well-known checkpoint names (`PerspectiveWorker.BeforeBatch`, `TransportConsumerWorker.BeforeHandle`, `OutboxDrain.BeforePublish`, etc.).
- **`IReceptorDedupStoreContractTests`** — abstract TUnit base with `[InheritsTests]`; the envelope-backed concrete subclass inherits 5 contract tests. A future DB-backed impl subclasses the same base for free.

## What did NOT ship — and why

The five scenarios the plan originally called out:

1. **Duplicate transport delivery** — simulate transport re-delivering the same `MessageId`; verify inbox PK + `ReceptorInvocations` guard produce a single receptor fire.
2. **Restart mid-lifecycle** — kill worker between outbox publish and inbox commit; verify `PostOutbox` count == 1 and `PostInbox` count == 1 on restart.
3. **20 perspectives / 4 batches** — verify `PostAllPerspectives` fires once and `PostLifecycle` fires once across a 20-perspective scenario processed in 4 batches of 5.
4. **Partial perspective failure** — 15 of 20 perspectives throw on first attempt; `PostAllPerspectives` does NOT fire; retry drives them to success and the completion fires exactly once.
5. **ServiceBus emulator round-trip** — dispatch → outbox → real ServiceBus emulator → inbox; `ReceptorInvocations` survives with matching `ServiceName`/`Duration`.

None shipped this round because each requires one or more of:

- **Invasive `PerspectiveWorker` modifications.** The worker is 1500+ lines on a hot path; adding `ChaosInjectorInvoker.BeforeCheckpointAsync` calls at the three relevant checkpoints (`BeforeBatch`, `AfterBatch`, `BeforeCompletionFire`) is straightforward but touches code that's shared by every perspective projection in every service. The in-scope regression risk was judged too high to do without a dedicated session.
- **Invasive `TransportConsumerWorker` modifications.** Same concern, similar size.
- **`OutboxDrain` modifications.** Requires understanding the drain path to plumb checkpoints without breaking batching semantics.
- **Real Postgres fixture setup.** The perspective scenarios (3, 4) need `PerspectiveWorker` running against a real DB; Whizbang integration tests already do this (`PerspectiveDedupIntegrationTests.cs` uses `FakeTimeProvider`) but building the right fixture for chaos injection is ~a day of work.
- **ServiceBus emulator orchestration.** Scenario 5 uses Docker. `Whizbang.Transports.AzureServiceBus.Integration.Tests/Containers/ServiceBusEmulatorFixture.cs` already provides this — extending it to assert on `ReceptorInvocations` across a cross-service dispatch is feasible but needs care.

## Coverage we DO have

The receptor-level contract is thoroughly covered by existing unit + integration tests from Phase 1–3:

- Same-stage duplicate fire → guardrail skips, EventId 18 warning (`ReceptorInvocationTrackingTests.SkipsAndWarnsWhenReceptorAlreadyFiredSameStageAsync`)
- Cross-stage duplicate fire → guardrail skips, EventId 18 with `PriorStage != CurrentStage` (`SkipsAndWarnsWhenReceptorAlreadyFiredPriorStageAsync`) — this covers the filter-bug case that is the underlying mechanism of the "duplicate transport delivery" scenario from the receptor's perspective
- Receptor exception → no record appended, retry can re-fire (`DoesNotRecordInvocationWhenReceptorThrowsAsync`) — the receptor-level side of "partial failure"
- `[ReceptorIdempotent]` bypass → receptor fires even with prior invocation (`ReceptorIdempotentBypassesGuardAsync`)
- Different receptors at same stage → both fire (`DifferentReceptorsAtSameStage_EachFireOnceAsync`)
- 100 interleaved concurrent envelopes → zero cross-contamination (`HundredMessagesInterleaved_NoCrossContaminationAsync`)
- Records survive `EnvelopeSerializer` + JSON string roundtrip (`ReceptorInvocationsRoundtripTests`)

Net effect: **the receptor-level exactly-once contract is locked in. The scenarios not yet written exercise worker-level behavior — retry coordination, batch completion, cross-service transport envelope fidelity — which isn't strictly a "does the guardrail work?" question.**

## How to pick this up

### Fast path: add ChaosInjector calls in PerspectiveWorker (~1 session)

`ChaosInjectorInvoker` is already a singleton in the DI container (registered by the generated `AddWhizbangReceptorRegistry`). To wire it into `PerspectiveWorker`:

1. Constructor-inject `ChaosInjectorInvoker?` (nullable — production won't register an injector).
2. Add three `await _chaos.BeforeCheckpointAsync(...)` calls:
   - `PerspectiveWorker._executeNormalPathAsync` at the top → `ChaosCheckpoints.PERSPECTIVE_WORKER_BEFORE_BATCH`
   - Same method at the bottom → `ChaosCheckpoints.PERSPECTIVE_WORKER_AFTER_BATCH`
   - Before the completion receptor dispatch → `ChaosCheckpoints.PERSPECTIVE_WORKER_BEFORE_COMPLETION_FIRE`
3. Add an integration test class (e.g., `tests/Whizbang.Core.Integration.Tests/ReceptorChaosScenarioTests.cs`) that:
   - Builds a service provider with `EnableChaosHooks = true` and a recording `IChaosInjector` under test control.
   - Drives `PerspectiveWorker` as done in `PerspectiveDedupIntegrationTests`.
   - For each scenario, schedules the injector to throw at the appropriate checkpoint.

### Followup path: wire TransportConsumerWorker + OutboxDrain

Mirror the pattern for the other workers using the already-defined checkpoint constants:
- `TransportConsumerWorker._handleMessageAsync` → `TRANSPORT_CONSUMER_BEFORE_HANDLE` / `TRANSPORT_CONSUMER_AFTER_HANDLE`
- Outbox drain publish path → `OUTBOX_DRAIN_BEFORE_PUBLISH` / `OUTBOX_DRAIN_AFTER_PUBLISH`
- Inbox commit path → `INBOX_BEFORE_COMMIT` / `INBOX_AFTER_COMMIT`

### ServiceBus roundtrip

Use the existing `ServiceBusEmulatorFixture`. The new test dispatches a command via the real transport, consumes it on the receiving side, and asserts the `envelope.ReceptorInvocations` list contains the expected records with correct `ServiceName` for each side. No chaos injector needed for this one — it's a fidelity test.

## Why this file exists

So a future session can pick up the chaos scenarios without re-deriving the design. The primitives are in place; the specific worker integrations are the remaining work. Scope it to a focused session rather than folding it into a broader PR.
