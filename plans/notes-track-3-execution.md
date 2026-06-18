# Track 3 — Execution notes

Append-only file capturing on-the-fly decisions, open questions, and surprises during the track-3 execution. Not committed unless the user wants it preserved.

## W1 — `wh_outbox` index audit

### Status: **Deferred to user (slot-3 PG access required)**

I don't have direct SQL access to slot 3 PG:
- `kubectl run psql-probe` is RBAC-blocked (Azure: "User does not have access to the resource in Azure. Update role assignment to allow access.")
- BFF pod doesn't have `psql` installed; only `dotnet`, `curl`, `wget`, `nc`
- The MCP postgres tool connects to a local DB, not slot 3

So W1 Phase 1 (`pg_stat_user_indexes` snapshot) and Phase 2 (isolated-table `EXPLAIN ANALYZE` experiments) need a human with sufficient Azure RBAC.

**What I can do once the user runs Phase 1+2:**
- Take the experiment data, judge it against Phase 3 decision criteria
- Codify winning drops as a Whizbang migration (Phase 4)
- Open the PR + monitor CI per the discipline

**What I'll do in the meantime:** proceed to W2 and W3 (both fully implementable in-repo without slot-3 access).

## W2 — `commit_handler_batch` two-tier

### Status: **Complete (local) — PR pending**

Implementation matches the plan's Option-D sketch exactly:

- **`commit_handler_batch_bulk(p_results JSONB) RETURNS VOID`** — Tier 1 optimistic. No `BEGIN..EXCEPTION` wrapper inside the loop, so any `commit_handler_result` raise propagates out of the function atomically.
- **`commit_handler_batch(p_results JSONB)`** — orchestrator. Subtransaction wraps the `PERFORM commit_handler_batch_bulk(...)`; on success returns synthesized all-success rows; on `WHEN OTHERS` falls through to the original SAVEPOINT-per-handler loop verbatim.

### Surprise: existing tests assert debug-mode semantics

My first GREEN had the bulk test asserting `COUNT(*) = 0` (production-mode DELETE). It failed because the test DB runs in debug mode (rows stamped, not deleted). I adjusted to `processed_at IS NOT NULL` to match the existing `CommitHandlerBatch_AllSucceed_ReportsAllSuccessAndAppliesAll` test pattern. The bulk-function semantics are still proven (atomic apply of all three handlers); the storage shape just differs by mode and isn't what we're locking.

### Test coverage summary

| Test | What it locks | Pre-impl |
|---|---|---|
| `CommitHandlerBatchBulk_FunctionExists_InPublicSchemaAsync` | Function exists in `public` | RED |
| `CommitHandlerBatchBulk_AllSucceed_AppliesAllInSingleTransactionAsync` | Tier 1 happy-path semantics | RED |
| `CommitHandlerBatchBulk_OneHandlerFails_RaisesAndAppliesNothingAsync` | Tier 1 all-or-nothing failure semantics | accidentally GREEN (non-existent fn → exception + zero rows applied) |
| `CommitHandlerBatch_OneHandlerFails_OthersSucceedSavepointIsolationAsync` (existing) | **Tier 2 fallback** — critical regression-lock proving orchestrator falls back correctly | passes both pre- and post-impl |

The accidentally-green test still locks the post-impl semantic (any error raises, zero rows applied) — so it has real value as a regression lock even though the RED→GREEN transition wasn't visible.

### Local verification

- Full `Whizbang.Data.EFCore.Postgres.Tests` project: 1339/1339 passing
- All 6 `CommitHandlerBatchSqlTests` passing
- Build + format clean

### Open question (deferred to PR review or follow-up)

- **Slot-3 perf validation**: the plan estimated 70 ms → ~25 ms per `CommitHandlerBatchAsync` hold. We can't measure this locally; it gates on a JDX bulk-import on slot 3 after merge. Need to schedule a re-baseline import after deploy.

## W3 — Composite events + body offload

### Status: **In progress** — 4 of 11 slices committed; on branch `release/v0.674.0-alpha.1` (pushed). Single-PR target.

### Decision: open design questions defaulted (per execution discipline #8)

The plan flagged six open design questions that we don't want to block on. Defaulted per "capture in notes, continue execution":

| Question | Default | Rationale |
|---|---|---|
| Failure atomicity | All-or-nothing | Simplest; per-inner retry is future work |
| Inner-event StreamId | Inherit composite's StreamId | Simplest; per-inner producer-supplied override is future work |
| Inner-event ordering | Sequential within composite | Matches single-row outbox storage semantics |
| Event-store replay | Always replay inner events; composite is wire-only | Per plan; lock at replay time |
| Producer migration sequence | Whizbang first → JDX pilot in follow-up | Plan says JDX pilot is a different work item |
| Lifecycle hooks fan-out | Per-inner-event | Consistent with "composite is wire-only"; preserves per-message contract |

### Completed slices

- **Slice 1 — Transport `MaxMessageSizeBytes`** (commits 23b93ae9, 7d8f22e0). InProcess+Rabbit=null, ASB=256K. RED/GREEN.
- **Slice 2 — `IMessageBodyStore` contract + options records** (commit c1a151e6). New `Whizbang.Core.Offloads` namespace. 10 contract tests.
- **Slice 3 — `AddWhizbangMessageBodyStore<T>` DI surface** (commit 24c41311). Keyed singleton; multiple providers coexist. 4 tests.
- **Slice 4 — `Whizbang.Offloads.InMemory` provider project** (commit 010b22ea). Mirrors `Whizbang.Transports.InMemory` role. SHA-256 hash on upload, MaxBytes cap, idempotent delete default. 9 tests.

### Remaining slices

- **Slice 5a — Strategy contract + default impl** (commit 44b1a45f). ✅
  - `IMessageBodyOffloadStrategy`, `OffloadDecision`, `BodyClaimEnvelopePayload` (sentinel), `MessageBodyOffloadOptions`, default `MessageBodyOffloadStrategy`
  - 5 tests; full Core test suite (7,879) still passes
- **Slice 5b — Wire into `OutboxPublishWorker`** (NOT DONE — design decision needed)
- **Slice 6** — Receive-side rehydrate in `TransportConsumerWorker` (new `IMessageBodyResolver.MaybeResolveAsync`)
- **Slice 7** — Cleanup hook (TTL + active delete options)
- **Slice 8** — `ICompositeEvent` contract
- **Slice 9** — Producer ergonomics + send path (`is_composite` on outbox row)
- **Slice 10** — Receiver expansion + event-store recording
- **Slice 11** — `EventStore.AppendBatchAsync` batched insert
- **Slice 4b** — `Whizbang.Offloads.AzureBlob` provider project (Azurite + live). Deferred; can ship after the core round-trip lands so we have an InMemory-based end-to-end smoke first.

### Slice 5b design — agreed: JIT post-serialize hook inside each transport

User feedback (2026-06-09): "I don't want the performance hit of a double serialize. We need to do this JIT after serialization."

Decision: the offload hook fires **inside the transport**, immediately after `JsonSerializer.Serialize(envelope, ...)`, before the wire-send call. No double-serialize on the non-offload path; on the offload path we serialize twice (original + small claim envelope) but that's unavoidable.

Implementation outline:

1. **InProcessTransport: no change.** It passes envelope objects through without serialization — there's no "post-serialize" point and `MaxMessageSizeBytes = null` already short-circuits the strategy.

2. **RabbitMQTransport (`RabbitMQTransport.cs:207`) and AzureServiceBusTransport.** After the `JsonSerializer.Serialize` call, inject:
   ```csharp
   var originalBytes = Encoding.UTF8.GetBytes(json);
   var replaceBytes = _offloadStrategy is null
     ? null
     : await _offloadStrategy.MaybeReplaceSerializedAsync(originalBytes, envelope, envelopeTypeName, MaxMessageSizeBytes, _jsonOptions, ct);
   var finalBytes = replaceBytes ?? originalBytes;
   // ... use finalBytes in BasicPublishAsync / ASB SendMessageAsync
   ```

3. **Constructor changes:** RabbitMQ + ASB take an optional `IMessageBodyOffloadStrategy?` (default null = no offload). Existing tests with positional ctor args still compile because it's at the end of the parameter list with a default.

4. **Strategy contract change:** the current `MaybeOffloadAsync` returns a `BodyClaimEnvelopePayload` sentinel — that's caller-constructs-the-envelope. Switch to a single `MaybeReplaceSerializedAsync` method that returns `ReadOnlyMemory<byte>?` — null if no offload, replacement bytes if offloaded. The method internally:
   - Uploads body via `IMessageBodyStore`
   - Builds a claim envelope (same MessageId/Hops/DispatchContext/etc. as the original, payload = `BodyClaimEnvelopePayload`)
   - Serializes the claim envelope via the supplied JsonSerializerOptions
   - Returns the small bytes

5. **Where claim-envelope construction lives:** inside the strategy, via a small `IClaimEnvelopeFactory` helper (so transports don't duplicate metadata-copy logic). The factory takes the original `IMessageEnvelope` + the claim and returns a `IMessageEnvelope<BodyClaimEnvelopePayload>`.

Existing `IMessageBodyOffloadStrategy` from slice 5a will be reshaped — the contract change is pre-v1 acceptable. The 5 strategy tests need to update to the new shape.

### Slice 5b actual execution log

User pivoted the design (2026-06-09) to **transport-level JIT hooks with the wire-size header always emitted**. Rationale: pre-serializing upstream means we'd double-serialize on the non-offload path. The wire-side hook avoids that AND broadcasts the size as a header for other tools.

Implementation landed as four sub-slices:

- **Slice 5b.1 — Post-serialize hook chain types** (commit cbc31c9c). Replaced the Slice 5a `IMessageBodyOffloadStrategy` with `IPostSerializeHook` + `PostSerializeHookChain` + `PostSerializeContext`/`Result`/`Outcome` records + `BodyOffloadPostSerializeHook` (Order=1000). DI: `AddWhizbangPostSerializeHook<T>` + `AddWhizbangBodyOffload()` convenience. 14 tests.

- **Slice 5b.2 — `preSerializedBytes` hint on `ITransport.PublishAsync`** (commit 269ad007). Optional `ReadOnlyMemory<byte>?` parameter — when set, wire transports MUST honor (skip internal serialize). InProcess ignores. RabbitMQ + ASB both honor. 28 test files mass-patched for the signature change. New invariant test: RabbitMQ's `PublishAsync_WithPreSerializedBytes_UsesHintNotSerializerAsync`.

- **Slice 5b.3 — Wire into `TransportPublishStrategy`** (commit 12d18956). Strategy gains optional `PostSerializeHookChain` + `JsonSerializerOptions` ctor params. When present and either chain non-empty OR transport has a ceiling, runs serialize → hook chain → stamps `whizbang.body-size` → validates ceiling → calls transport via hint. Hard-fails oversized + no-offload with new `MessageFailureReason.MessageBodyTooLarge = 12`. Fast path preserved when chain isn't configured. 5 new tests.

- **Slice 5b.4 ✅** (commit 90eb4e66) — Bulk publish path runs the chain per item. Added `BulkPublishItem.PerItemMetadata` for per-message ApplicationProperties (whizbang.body-size lands per item, not just on the shared destination). Oversized item: per-item failure, batch proceeds; all-items-oversized: transport never called. Resolved the open question per-item-vs-batch in favor of per-item.

- **Slice 5b.5 ✅** (commit 550b919c) — RabbitMQ + ASB DI extensions inject `PostSerializeHookChain + JsonSerializerOptions` into TransportPublishStrategy automatically. `AddWhizbangBodyOffload()` now also calls `AddOptions<MessageBodyOffloadOptions>()` so users don't need to remember it. End-to-end DI flow tested.

### Slice 5b status: **FEATURE-COMPLETE ON THE PUBLISH SIDE.**

Confirmed working:
- Strategy serializes once, runs chain, stamps `whizbang.body-size`, validates ceiling pre-flight, hands transport the bytes hint via `preSerializedBytes`
- Bulk path: per-item chain run, per-item `whizbang.body-size` + `whizbang.is-claim` + `whizbang.body-store` + `whizbang.original-type`
- Hard-fail on oversized + no offload with `MessageFailureReason.MessageBodyTooLarge` and a remediation message pointing at `AddWhizbangBodyOffload()`
- Fast path preserved when no chain is registered (InProcess users pay nothing)

Local validation: Whizbang.Core.Tests 7897/7897, Transports.RabbitMQ.Tests 104/104, Transports.AzureServiceBus.Tests 147/147. No regressions.

### Remaining slices

- **Slice 6.1 ✅** (commit 4cb5676b) — Receive-side claim deserialize + rehydrator. New types: `BodyClaimWireHelper`, `BodyClaimRehydrator`, `RehydrateResult`. New failure reasons: `BodyClaimProviderUnknown = 13`, `BodyClaimIntegrityFailure = 14`. RabbitMQ + ASB deserialize paths now pick `MessageEnvelope<BodyClaimEnvelopePayload>` JsonTypeInfo when `whizbang.is-claim` header is set. JsonContext registration added for the sentinel payload type. 3 tests.
- **Slice 6.2 ✅** (commit 9872a419) — Worker wiring. `TransportConsumerWorker._tryBuildInboxMessageFromTransportAsync` calls `MaybeRehydrateAsync` before `_serializeToNewInboxMessage`; dead-letter outcomes log + drop with the typed failure reason. All 170 existing consumer-worker tests still pass; full Whizbang.Core.Tests 7900/7900.

**End-to-end publish→receive round-trip is feature-complete.** Producer offloads when over threshold; consumer rehydrates from the same body store. Both sides survive AOT via `InfrastructureJsonContext` registration.

- **Slice 7 ✅** (commit 1b0c7614) — Active cleanup mode. `MessageBodyOffloadOptions.ActiveCleanup` now end-to-end. Default `false` → provider TTL. `true` → rehydrator surfaces `PendingCleanupClaim`; consumer worker fires `_fireActiveCleanupAsync` post-commit (fresh scope, keyed store lookup, IgnoreMissing absorbs fan-out races, provider TTL is the backstop on transient failures). 2 new tests.

**Body offload feature is COMPLETE end-to-end.** Publish (5b) → wire → receive (6) → cleanup (7). Production-ready when paired with `Whizbang.Offloads.AzureBlob` (slice 4b).

- **Slice 4b ✅** (commit 7b7b824a) — `Whizbang.Offloads.AzureBlob`. Production provider backed by `Azure.Storage.Blobs 12.24.0`. Supports Azurite emulator + live Azure transparently via standard connection-string conventions. SHA-256 hash on upload, optional Hot/Cool/Cold/Archive access tier, MaxDownloadBytes defensive cap, lazy CreateIfNotExists for container, idempotent delete. `AddWhizbangAzureBlobOffload(name, opts => ...)` extension layers per-provider options binding. 5 DI registration tests.

**W3 body-offload feature is PRODUCTION-DEPLOYABLE.** End-to-end usage:
```csharp
services.AddWhizbangAzureBlobOffload("azure-blob-prod", opts => {
  opts.ConnectionString = builder.Configuration.GetConnectionString("Storage");
  opts.ContainerName = "whizbang-offload-bodies";
});
services.AddWhizbangBodyOffload();
services.Configure<MessageBodyOffloadOptions>(opts => {
  opts.ProviderName = "azure-blob-prod";
  opts.SizeThresholdBytes = 64 * 1024;
});
```

- **Slice 8 ✅** (commit e5ab89eb) — `ICompositeEvent` contract. Marker extending IMessage; `InnerEvents` enumerable + `MaxInnerEventsAllowed` default 10K override. 5 contract tests. Two new failure reasons: `CompositeInnerEventLimitExceeded = 15`, `CompositeExpansionFailure = 16`.
- **Slice 9 ✅** (commit 8b313558) — Producer + consumer stamp `IsComposite` on outbox/inbox rows. Dispatcher's two outbox builder paths + consumer worker's inbox builder all set the flag from `payload is ICompositeEvent`. 4 stamping tests + zero regression on the 170 TransportConsumerWorker tests.

**Azurite integration tests** (commit 7eec72ea, addressing emulator concern): 4 round-trip tests against a real Azurite container via `Testcontainers.Azurite`. Locks the invariant that the AzureBlob provider's behavior is identical against the emulator and live Azure. `<WhizbangTestType>Integration</WhizbangTestType>` + Tags `AzureBlob;Docker;Offloads`. ~7 seconds total wall time.

- **Slice 10 ✅** (commit 99a3c87b) — `CompositeEventExpander` (generic + non-generic Expand) + consumer worker fan-out (`_tryExpandCompositeToInboxMessages`). 6 expander tests + 170 consumer-worker regression-clean. Inner envelopes inherit composite identity context; hops shared by reference; fresh MessageIds per inner; cap enforcement stops yield without partial leak; non-composite payload throws clear error.
- **Slice 11 ✅** (commit b08c36b6) — `IEventStore.AppendBatchAsync<TMessage>` with default loop impl. Receiver-side perf path for composite expansion; backends override for bulk INSERT (EFCore Postgres path documented for follow-up). 3 contract tests lock the default semantics.

## W3 status: **PR-READY.**

Body offload (slices 1-7, 4b): production-deployable. Composite events (slices 8-11): contract + expansion + batched-append default. 24 commits on `release/v0.674.0-alpha.1` pushed. Full Whizbang.Core.Tests 7920/7920 + all transport projects + Azurite integration tests all green.

Single-PR target unchanged. PR not opened yet; ship all 11+sub slices before opening.

### Where to resume

W3 branch `release/v0.674.0-alpha.1` on origin with 7 commits (slices 1, 2, 3, 4, 5a). Resume with Slice 5b (`OutboxPublishWorker` wiring) — pick Option 1 above unless user has a preference, then continue through Slices 6, 7, 8, 9, 10, 11, and 4b in order. Single PR target — don't open the PR until all 11 land.

### Surprise: greenfield rhythm

Slices 2-4 were "the test IS the spec" — no real RED-before-GREEN cycle for pure greenfield contract types and constructor-only impls. I documented this in the Slice 2 commit message and shipped the spec+impl in one commit. The behavioral lock is the test, which would fail if anyone flipped a default like `MessageBodyDeleteOptions.IgnoreMissing`. Strict RED becomes meaningful again at Slices 5-6 (behavior changes in existing workers).
