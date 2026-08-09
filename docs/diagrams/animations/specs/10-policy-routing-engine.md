# Policy Routing Engine — Animation Spec

**Animation file:** `docs/diagrams/animations/10-policy-routing-engine.html`
**Steps:** 7
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** How Whizbang's policy engine determines where a message goes — which topic, which stream key, which partition number, and which execution strategy. It demonstrates the first-match-wins evaluation model and why policy registration order matters.

**Why it matters:** Every message routed through the outbox or transport passes through this engine. Developers configuring message policies, debugging routing surprises, or reading a `PolicyDecisionTrail` in production traces need this mental model.

**Intended audience:** Application developers writing policy configurations; platform engineers debugging message routing; anyone reading a `PolicyDecisionTrail` in a `MessageHop`.

**Conceptual prerequisite:** Basic understanding that Whizbang routes events to transport via policies configured in `Program.cs`.

---

## 2. Visual Layout

Three-column grid (`grid-template-columns: 280px 1fr 220px`):

| Column | DOM IDs | Represents |
|--------|---------|------------|
| Left (280px) | `.policy-list`, `pol1`–`pol4` | Ordered list of configured policies |
| Center (flex) | `n-message`, `n-ctx`, `rc-match`, `rc-config`, `rc-partition`, `n-trail` | Message arrival → context build → match result → configuration → partition calc → trail |
| Right (220px) | `out-topic`, `out-stream`, `out-partition`, `out-executor`, `out-transport` | Final routing result slots |

**Policy card states** (`pol1`–`pol4`):
- Default: `opacity: 0.6`, no border highlight
- `.evaluating`: cyan border + glow box-shadow
- `.matched`: green border, `var(--phase-perspective-bg)` background
- `.skipped`: `opacity: 0.3`, dashed border

**Result card visibility** (`rc-match`, `rc-config`, `rc-partition`): hidden (`opacity: 0`, `translateY(4px)`) until `.visible` applied.

**Output slot visibility** (`out-*`): hidden until both `.visible` and `.active` applied.

**Reset:** `resetAll()` — removes `.glow`/`.active`/`highlight-success` from nodes, `.evaluating`/`.matched`/`.skipped` from policy cards, `.visible` from result cards and output slots, resets all value spans to `—`.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `PolicyContext` | `src/Whizbang.Core/Policies/PolicyContext.cs` | Properties: `Message`, `MessageType`, `Envelope`, `Environment`, `Services`; step 2 lists these |
| `PolicyContext.MatchesAggregate<T>()` | `src/Whizbang.Core/Policies/PolicyContext.cs` | Implementation uses name-based convention (`MessageType.Name.Contains(aggregateName)`), NOT attribute lookup — see accuracy gap in step 3 |
| `PolicyContext.GetAggregateId()` | `src/Whizbang.Core/Policies/PolicyContext.cs` | Returns aggregate ID from message; step 5 shows `"order-" + ctx.GetAggregateId()` |
| `IPolicyEngine` / `AddPolicy` | `src/Whizbang.Core/Policies/IPolicyEngine.cs` | Evaluation order (first-match-wins); steps 3–4 |
| `PolicyConfiguration` fluent API | `src/Whizbang.Core/Policies/PolicyConfiguration.cs` | Method names: `UseTopic()`, `UseStreamId()`, `WithPartitions()`, `UsePartitionRouter<T>()`, `UseExecutionStrategy<T>()`; step 5 narration lists all five |
| `HashPartitionRouter` | `src/Whizbang.Core/Partitioning/HashPartitionRouter.cs` | Actual algorithm: FNV-1a hash (NOT PostgreSQL `hashtext()`) — see accuracy gap in step 6 |
| `IPartitionRouter.SelectPartition` | `src/Whizbang.Core/Partitioning/IPartitionRouter.cs` | Signature: `SelectPartition(string streamKey, int partitionCount, PolicyContext context)` |
| `compute_partition()` SQL | `src/Whizbang.Data.Postgres/Migrations/001_CreateComputePartitionFunction.sql` | Uses `abs(hashtext(p_stream_id::TEXT)) % p_partition_count` — the formula in step 6 narration is this SQL variant |
| `PolicyDecisionTrail` | `src/Whizbang.Core/Policies/PolicyDecisionTrail.cs` | Fields on trail; step 7 references recording it on `MessageHop` |
| `MessageHop.Trail` | `src/Whizbang.Core/Observability/MessageHop.cs` | JSON property `"tr"`, type `PolicyDecisionTrail?`; step 7 |
| `DispatchModes.Local` / `DispatchModes.Outbox` | `src/Whizbang.Core/Dispatch/DispatchMode.cs` | Step 7 aside: owned-domain commands stay local (`Local`) vs events go to transport (`Outbox`) |
| `DefaultRoutingAttribute` | `src/Whizbang.Core/Dispatch/DefaultRoutingAttribute.cs` | Referenced in step 6 of `07-end-to-end-flow` not here, but part of the full dispatch picture mentioned in step 7 |
| `Dispatcher._isOwnedNamespace()` | `src/Whizbang.Core/Dispatcher.cs` | The "owned-domain commands stay local" behavior in step 7 is implemented here |

---

## 4. Steps Specification

### `msg-arrive` — Message Arrives (2500ms)

**Narration:** An `OrderPlacedEvent` arrives at the policy engine. It belongs to the Order aggregate with AggregateId = order-789.

**DOM on enter:** `n-message` gets `.glow`
**DOM on exit:** `resetAll()`

**Source symbols:** none — setup step

**Intent:** Establishes the two inputs that drive policy matching: message type and aggregate identity.

---

### `ctx-build` — Build PolicyContext (2500ms)

**Narration:** `PolicyContext` created with access to: `Message` (the event), `MessageType`, `Envelope` (with metadata/tags), `Environment`, `Services` (DI container).

**DOM on enter:** `n-ctx` gets `.glow`
**DOM on exit:** `resetAll()`

**Source symbols:** `PolicyContext` — properties `Message`, `MessageType`, `Envelope`, `Environment`, `Services`

**Intent:** Shows that predicates have access to the full message and DI container, enabling complex routing rules.

---

### `eval-pol1` — Evaluate Policy 1 (3000ms)

**Narration:** Policy 1: `ctx.MatchesAggregate<Order>()` — checks if the message type is associated with the Order aggregate. OrderPlacedEvent IS an Order aggregate message. MATCH! First-match-wins: remaining policies are skipped.

**DOM on enter:** `pol1` gets `.evaluating`; after 1200ms timeout: `.evaluating` removed, `.matched` added
**DOM on exit:** `resetAll()`

**Source symbols:** `PolicyContext.MatchesAggregate<T>()`

**Intent:** Shows predicate evaluation and the moment of match. The two-phase visual (evaluating → matched) represents predicate execution completing.

**Known accuracy gap:** The narration says "checks if the message type is associated with the Order aggregate." The actual implementation in `PolicyContext.MatchesAggregate<T>()` uses name-based convention — it checks if `MessageType.Name` contains the aggregate type name using `OrdinalIgnoreCase`. There is no attribute registration or formal association mechanism. If the implementation changes to attribute-based lookup, the narration "associated with" wording would become accurate but the step 5 configuration example might need updating.

---

### `skip-rest` — Skip Remaining Policies (2200ms)

**Narration:** First-match-wins: Policies 2, 3, and 4 are never evaluated. This is why policy ordering matters — most specific policies should come first, catch-all last.

**DOM on enter:** `pol1` gets `.matched`; `pol2`, `pol3`, `pol4` get `.skipped`
**DOM on exit:** `resetAll()`

**Source symbols:** `IPolicyEngine.MatchAsync` — stops after first predicate returns true

**Intent:** Makes the short-circuit behavior viscerally clear through visual dimming. The ordering guidance (specific → general) is the key takeaway.

---

### `apply-config` — Apply Configuration (3000ms)

**Narration:** Policy 1 configuration applied: `UseTopic("orders")`, `UseStream(ctx => "order-" + ctx.GetAggregateId())`, `WithPartitions(16)`, `UsePartitionRouter<HashPartitionRouter>()`, `UseExecutionStrategy<SerialExecutor>()`.

**DOM on enter:** `pol1` `.matched`; `pol2`–`pol4` `.skipped`; `rc-match` `.visible` with values; `rc-config` `.visible` with: topic=orders, stream=order-789, partitions=16, router=HashPartitionRouter, strategy=SerialExecutor
**DOM on exit:** `resetAll()`

**Source symbols:** `PolicyConfiguration` — `UseTopic()`, `UseStreamId()`, `WithPartitions()`, `UsePartitionRouter<T>()`, `UseExecutionStrategy<T>()`; `PolicyContext.GetAggregateId()`

**Intent:** Shows the complete policy configuration that was registered. The stream value `order-789` is the result of calling `ctx.GetAggregateId()` on the arriving message.

---

### `partition-calc` — Partition Assignment (3000ms)

**Narration:** `HashPartitionRouter` computes: `abs(hashtext("order-789")) % 16`. The stream key is hashed to a deterministic partition number. All messages for order-789 always go to the same partition, guaranteeing ordering.

**DOM on enter:** `pol1` `.matched`; all three result cards `.visible` with values; `rc-partition` shows formula=`abs(hashtext("order-789")) % 16`, partition=`11`
**DOM on exit:** `resetAll()`

**Source symbols:** `HashPartitionRouter.SelectPartition()`, `compute_partition()` SQL

**Known accuracy gap:** The formula `abs(hashtext("order-789")) % 16` is the PostgreSQL `compute_partition()` function's formula. The C# `HashPartitionRouter` uses FNV-1a, which produces a different numerical result. The partition number `11` is illustrative. A developer verifying the actual partition using the C# router will get a different number. This is an intentional simplification — the concept (deterministic hashing to a fixed partition) is accurate; the formula shown is the SQL variant. Do not "fix" this by computing the FNV-1a result; instead, add a note if the discrepancy causes confusion.

---

### `output` — Routing Result (2500ms)

**Narration:** Final routing: topic=orders, stream=order-789, partition=11, serial execution. Note: owned-domain commands (matching the service namespace) stay local instead of going to outbox — events always go to transport. The `PolicyDecisionTrail` is recorded on the `MessageHop`.

**DOM on enter:** `pol1` `.matched`; all five output slots `.visible`+`.active` with values; `n-trail` gets `.glow`
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageHop.Trail` (`PolicyDecisionTrail?`); `DispatchModes.Local` vs `Outbox`; `Dispatcher._isOwnedNamespace()`

**Intent:** Shows the committed routing outcome and introduces the observability trail. The aside about owned-domain commands bridges to the dispatch mode system without derailing the main policy routing narrative.

---

## 5. Maintenance Guide

**`PolicyContext` properties change** (`src/Whizbang.Core/Policies/PolicyContext.cs`):
- If properties listed in step 2 change names or are removed → update step 2 narration
- If `MatchesAggregate<T>()` changes from name-convention to attribute-based → update step 3 narration and accuracy gap note
- If `GetAggregateId()` signature changes → update step 5 narration

**`PolicyConfiguration` fluent API changes** (`src/Whizbang.Core/Policies/PolicyConfiguration.cs`):
- If any method in step 5 is renamed (`UseTopic`, `UseStreamId`, `WithPartitions`, `UsePartitionRouter<T>`, `UseExecutionStrategy<T>`) → update step 5 narration
- If new required config fields are added → add to step 5

**Policy evaluation model changes** (`src/Whizbang.Core/Policies/IPolicyEngine.cs`):
- If first-match-wins changes to scored/priority matching → steps 3, 4, and 5 need full rewrites; the HTML column header "evaluated top → bottom, first match wins" also changes

**`HashPartitionRouter` algorithm changes** (`src/Whizbang.Core/Partitioning/HashPartitionRouter.cs`):
- If algorithm changes from FNV-1a → update step 6 accuracy gap note
- If C# router is aligned to use `hashtext()` to match SQL → the formula in step 6 becomes accurate for both implementations; update accuracy gap note to reflect this

**`MessageHop.Trail` changes** (`src/Whizbang.Core/Observability/MessageHop.cs`):
- If `Trail` property removed or renamed → step 7 narration breaks

**`DispatchModes` / owned-domain routing** (`src/Whizbang.Core/Dispatch/DispatchMode.cs`, `src/Whizbang.Core/Dispatcher.cs`):
- If the "owned-domain commands stay local" behavior changes or is removed → update step 7 aside

**What does NOT require an update:**
- Changes to `MessageEnvelope`, `MessageHop` fields not involving `PolicyDecisionTrail`
- Changes to `OutboxRecord`, `InboxRecord`, `LifecycleStage`
- Transport configuration changes (Kafka / RabbitMQ topic names — these are user-space examples)
- Changes to `AwaitPerspectiveSyncAttribute`, `ILifecycleCoordinator`, or perspective types
