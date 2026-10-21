# WS-2: event slimming

Part of [storage-reclamation.md](storage-reclamation.md). Release: 1.0.

## Context

Event bodies and pointers are half of each database in the evidence deployment, and much of it is
bytes nobody reads:

- High-frequency session/presence events and per-item saga progress events are stored as durable
  history although no perspective ever replays them.
- Audit records carry the full body of the audited event (`EventAudited.OriginalBody`,
  `src/Whizbang.Core/SystemEvents/EventAudited.cs:98`), including events carrying ~24 KB embedding
  vectors and full document snapshots. Audit was 25% of body bytes in the busiest service. The
  built-in audit projection keeps only a pointer and scope (`AuditEventProjection.Apply`,
  `src/Whizbang.Core/SystemEvents/Audit/AuditEventModel.cs:116`); the copy exists only because a
  subscriber cannot read the origin's store.
- Stored metadata is about 600 B per event, nearly all routing hops copied verbatim from the
  envelope by the emit chain.
- About 83% of bodies sit under the TOAST threshold and are never compressed.
- Types whose every event carries the whole state for its key (embeddings, permission snapshots)
  keep every superseded version forever.

## Goal

Stop writing bytes nobody reads, compress what remains, and let full-state types keep only their
latest event, without weakening linkage, scope, chains or integrity.

## Slices

Ordered. Each slice is one red/green increment and one PR unless noted.

### S1. Defect 2: `deliveryGuarantee` reaches stored metadata

- **Changes.** The emit chain builds stored metadata as `{MessageId, Hops}` plus
  `ephemeral_expires_at` only (`src/Whizbang.Data.Postgres/Migrations/149_MessagePriority.sql:546-555`
  outbox, `162_InboxWorkStateSideTable.sql:2016-2027` inbox). Schedule occurrences put
  `deliveryGuarantee` on `wh_outbox.metadata` (`068_ClaimDueSchedules.sql:62`), so every filter
  reading `body_meta`/`eb.metadata ->> 'deliveryGuarantee'` (149:622, 162:2106, 093, 180:255,
  `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1565`) sees null. A new migration
  redefines both chains whole (rule 5) and copies `deliveryGuarantee` into `body_meta` when present.
- **Done differently (#1484, migration 214).** Not a metadata key: the guarantee is bit 5 of the row's
  `flags` (`EventFlags.AtMostOnce` = 32), set by `_wh_spawn_occurrence` and copied by the emit chain,
  so it costs no stored bytes (in line with S8) and every filter is a bit test with no join to
  `wh_event_body`. 214 also flags rows stored before it from `wh_schedule_runs` and corrects the
  digests and sealed epochs they were folded into.
- **RED first.** In `tests/Whizbang.Data.EFCore.Postgres.Tests/EmitChainDigestFold.cs`: an
  at-most-once occurrence stored through the real outbox path is absent from `wh_stream_digests`;
  the same through the inbox path; a normal event is still folded. Existing digest tests seed
  metadata by hand (`DigestEpochSqlTests.cs:93`) and so cannot catch this. Add one
  `SelectRedeliveryEventsAsync` test that an at-most-once event written by the emit chain is not
  selected.
- **Docs.** `v1.0.0/resilience/stream-integrity.md` (at-most-once exclusion is now real).
- **Config.** None.

### S2. A1: ephemeral classification guidance and reconcile

- **Changes.** The mechanism ships (`[Ephemeral]`, `src/Whizbang.Core/Attributes/EphemeralAttribute.cs:40`;
  reclassification, `src/Whizbang.Core/Fingerprint/TypeDefinitionReconciler.cs:172-182`). Two gaps:
  1. No published page tells a developer which events should be ephemeral. The attribute's
     `<docs>` already points at `fundamentals/events/ephemeral-events`, which exists only as a
     proposal.
  2. Reclassification acts only on the boot that detects the drift. Verify that a type turned
     ephemeral while `ReconcileHistoricalOnStartup` was off is still reclassified when the flag is
     turned on later; if not, persist the pending reclassification and act on it.
- **RED first.** In `tests/Whizbang.Core.Tests/Fingerprint/TypeDefinitionReconcilerCoverageTests.cs`:
  drift recorded with the flag off, then a later boot with the flag on, reclassifies; flag on at
  detection reclassifies once; a non-event kind never reclassifies.
- **Docs.** New `v1.0.0/fundamentals/events/ephemeral-events.md`, graduated from
  `proposals/ephemeral-events.md`, with a classification guide: events that are presence, progress,
  heartbeats or derived signals, that no perspective rebuild needs, and that are superseded within
  minutes. Update `v1.0.0/operations/configuration/configuration-reference.md` (EphemeralOptions).
- **Config.** Existing `Whizbang:Ephemeral:ReconcileHistoricalOnStartup`
  (`Whizbang__Ephemeral__ReconcileHistoricalOnStartup`, default `false`).

### S3. A2a: `EventAudited` field defects (defect 3, part 1)

- **Changes.** Three writers build `EventAudited`:
  `AuditOutboxMessageBuilder.cs:73-87`, `AuditingEventStoreDecorator.cs:160-176`,
  `SystemEventEmitter.cs:83-96` (all in `src/Whizbang.Core/SystemEvents/`). The last two never set
  `OriginalEventId`; none sets `CausationId` (`EventAudited.cs:123`); the outbox writer hard-codes
  `OriginalStreamPosition = 0` (`AuditOutboxMessageBuilder.cs:78`). Set the id and causation in all
  three. For the position on the outbox path (the version is assigned later, in the emit chain),
  apply the maintainer's choice from the open questions.
- **RED first.** In the existing `AuditOutboxMessageBuilderTests.cs`,
  `AuditingEventStoreDecoratorTests.cs`, `SystemEventEmitterCoverageTests.cs`: one test per writer
  per field.
- **Docs.** `v1.0.0/fundamentals/events/system-events.md` (EventAudited section).
- **Config.** None.

### S4. A2b: the outbox path consults `IAuditDecisionHook` (defect 3, part 2)

- **Changes.** `TryBuildAuditMessage` accepts a hook (`AuditOutboxMessageBuilder.cs:30`), but both
  callers omit it (`src/Whizbang.Core/Messaging/WorkCoordinatorQueues.cs:46`,
  `StreamAffinityWorkCoordinatorStrategy.cs:73`). Inject the registered hook into both.
- **RED first.** Per caller: a hook that vetoes suppresses the audit message; a hook with no opinion
  leaves the attribute's decision; a hook that renames sets `ActivityName`.
- **Docs.** `v1.0.0/fundamentals/security/audit-logging.md`.
- **Config.** None.

### S5. A2c: pointer + scope by default, body opt-in

**Status (2026-10-21):** the registry half ships in #1580; the body opt-in (`IncludeBody`,
`AuditBodyDefault`, nullable `OriginalBody`, all three writers) ships in #1588.

- **Changes.** Add `IncludeBody` to `AuditEventAttribute`
  (`src/Whizbang.Core/Attributes/AuditEventAttribute.cs:39`, beside `Exclude` at :82). The three
  writers omit `OriginalBody` unless the type opts in or the global default says include.
  `OriginalBody` is `required JsonElement` today, so it becomes optional; the tag payload already
  leaves it out (`EventAudited.cs:56-60`). `AuditEligibility` reads `[AuditEvent]` by reflection
  today (`GetCustomAttributes`, `AuditEligibility.cs:37`), which the AOT rule forbids in Core;
  reading `IncludeBody` must not add to that, so this slice moves the attribute's values into a
  generated per-type registry and `AuditEligibility` reads from it.
- **RED first.** Default type: body absent, pointer fields present. `IncludeBody = true`: body
  present. Global default `Include`: body present for every type. The built-in projection still
  produces the same `AuditEventModel` with and without a body. Generator: the registry carries
  `Exclude`, `Reason`, `Level` and `IncludeBody` for every attributed type, and an unattributed type
  is absent.
- **Docs.** `v1.0.0/fundamentals/security/audit-logging.md` (new "What an audit record stores"
  section); `system-events.md`; configuration reference (SystemEventOptions).
- **Config.** `Whizbang:SystemEvents:AuditBodyDefault` (`Whizbang__SystemEvents__AuditBodyDefault`,
  default `Omit`; values `Omit`, `Include`). Bound with the existing section
  (`src/Whizbang.Core/Configuration/ProcessWideOptionsBinding.cs:151`).

### S6. A2d: one audit ledger stream per tenant (defect 3, part 3)

- **Changes.** Decided earlier and never shipped. Each single is minted on its own stream
  (`AuditOutboxMessageBuilder.cs:140`, `StreamId = auditEvent.Id`) and each folded composite on
  another fresh one (`src/Whizbang.Core/Workers/CoalesceShipWorker.cs:259`). Stamp
  `UUIDv5("sys-audit", tenant)` on the single and let the fold inherit it. No name-based UUID helper
  exists in Core (`SystemEventStream.cs:36` mentions UUIDv5 but uses a constant at :42), so add an
  AOT-safe one.
- **RED first.** Helper: known vectors, same input same id, different tenant different id, null
  tenant maps to the empty-tenant ledger. Builder: two records for one tenant share a stream. Fold:
  a composite inherits the ledger stream; a fold never mixes tenants.
- **Docs.** `audit-logging.md` ("Dedicated System Stream" section).
- **Config.** None.

### S7. A2e: audit is excluded from digests (defect 10) and re-derivable

- **Changes.** Audit is a pure function of the origin event plus scope, so audit rows never need
  digest sync. On the subscriber a composite-fanned `EventAudited` folds into the origin's digest
  lane (`162_InboxWorkStateSideTable.sql:2091-2106`) where the origin has no matching rows. Exclude
  audit system events from both digest folds. Then add an audit backfill: a redelivery
  (`RedeliveryRequest`, `src/Whizbang.Core/Messaging/Redelivery.cs:27`) filtered by audit
  eligibility (`AuditEligibility.Decide`, `AuditEligibility.cs:27`) that re-mints records. Audit ids
  become name-based on `OriginalEventId` so a re-derived record deduplicates against the original.
- **RED first.** SQL: an inbox-stored audit composite leaves `wh_stream_digests` untouched; a
  domain event in the same batch still folds. C#: backfill selects only eligible types; re-deriving
  twice yields one record per origin event.
- **Docs.** `audit-logging.md` (new "Rebuilding the audit ledger"); `stream-integrity.md`.
- **Config.** None.

### S8. A3a: a slim stored-metadata form readers accept

- **Changes.** Define the slim form. **Must keep:** a non-null object; `MessageId`
  (`required`, `src/Whizbang.Core/Observability/EnvelopeMetadata.cs:17`); the current hop's `Scope`;
  `CorrelationId` and `CausationId`; the last hop's `Timestamp`; `ephemeral_expires_at`;
  `deliveryGuarantee` (S1). **Safe to strip:** `ServiceInstance`, `Topic`, `PartitionIndex`,
  `SequenceNumber`, `ExecutionStrategy`, `Caller*`, `Duration`, `Trail`, `TraceParent`, the hop
  `Metadata` dictionary, causation-type hops, `ReceptorInvocations`, `DispatchContext`.
  `MessageHop.ServiceInstance` is `required` (`MessageHop.cs:93-94`), so deserializing a slim hop
  throws today; relax it (or keep a minimal stub) before any writer slims.
- **RED first.** A slim document round-trips through the source-generated context into
  `EnvelopeMetadata`; a perspective apply over a slim event sees the same scope, correlation and
  causation as over the full one; a document missing `MessageId` still fails loudly.
- **Docs.** `v1.0.0/messaging/message-envelopes.md` (stored versus wire form).
- **Config.** None.
- **Status (2026-10-21):** ships in #1586 as `EnvelopeMetadata.ToSlim()`. Correction to the list above:
  readers take the message timestamp, correlation and causation from the **first** hop
  (`MessageEnvelope.GetMessageTimestamp`, `GetCorrelationId`, `GetCausationId`), so the slim hop copies
  them from there, not from the last hop. The scope is folded over every Current hop into one delta.
  `ServiceInstance` stays `required`; a missing `si` reads as `ServiceInstanceInfo.Unknown`.

### S9. A3b: slim at write time

- **Changes.** A new migration redefines both emit chains (149:469 outbox, 162:1918 inbox) to build
  the slim form when `wh_settings.metadata_write_form = 'slim'`. Full hops stay on the outbox and
  inbox rows for delivery. The direct append paths slim in C# before writing
  (`src/Whizbang.Data.Dapper.Postgres/DapperPostgresEventStore.cs:194`,
  `src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs:161`). The emit chain is hot-path SQL:
  measure on a copied table first.
- **RED first.** SQL tests per chain: setting `slim` stores only must-keep fields; setting `full`
  stores today's shape; a missing setting row behaves as the default. C# tests for each direct path.
- **Docs.** `v1.0.0/data/event-store.md` (Event Stream Schema); configuration reference
  (Database-side settings).
- **Config.** `Whizbang:EventStore:Metadata:WriteForm`
  (`Whizbang__EventStore__Metadata__WriteForm`, default per open question; values `Full`, `Slim`),
  synced to `metadata_write_form` by a bridge modeled on
  `src/Whizbang.Core/Workers/DebugRetentionBridge.cs:31`. New options class
  `EventStoreMetadataOptions` bound from a literal `GetSection("Whizbang:EventStore:Metadata")`.

### S10. A3c: metadata retention for history

- **Changes.** A batched task in `perform_maintenance` (latest definition
  `209_SnapshotsCarryTheirStreamVersion.sql:103`, beside the ephemeral body reaper at :364) rewrites
  `wh_event_body.metadata` to the slim form for rows older than N days when the policy is
  `SlimAfter`. A one-time rewrite of history costs WAL and dead tuples roughly equal to the metadata
  it touches; batch it and let WS-4 reclaim the space.
- **RED first.** `KeepAll` touches nothing; `SlimAfter` slims only rows past the horizon; a slimmed
  row is not touched again; debug mode skips the task; the task reports `rows_affected`.
- **Docs.** Configuration reference (MaintenanceWorkerOptions and Database-side settings).
- **Config.** `Whizbang:EventStore:Metadata:Retention` (`Whizbang__EventStore__Metadata__Retention`,
  default `KeepAll`; values `KeepAll`, `SlimAfter`) and `Whizbang:EventStore:Metadata:SlimAfterDays`
  (`Whizbang__EventStore__Metadata__SlimAfterDays`, default `30`), synced to
  `metadata_retention_policy` and `metadata_slim_after_days`.

### S11. A6: `toast_tuple_target` on `wh_event_body`, measured first

- **Changes.** `toast_tuple_target` is per table, not per column, and lz4 is often already the
  server default. Lowering it on `wh_event_body` (created at `072_EphemeralBodyOffload.sql:19`,
  minimum 128) should make PostgreSQL compress the widest columns until the row fits. **Measure
  first** on a copied table: in the heap insert path, toasting is entered only when a tuple exceeds
  the compile-time `TOAST_TUPLE_THRESHOLD` (about 2 KB), so a lower target may have no effect on the
  small rows that make up 83% of bodies. If it works, a migration sets the target and
  `SET COMPRESSION lz4` on both columns (the same DDL the perspective generator emits,
  `src/Whizbang.Generators.Shared/Models/ColumnStorageSql.cs:154-169`). Existing rows change only on
  rewrite (WS-4).
- **RED first.** Schema test: `pg_class.reloptions` for `wh_event_body` carries the target after
  migration and after a second boot. If the measurement holds: an inserted 1 KB body has
  `pg_column_size < octet_length`.
- **Docs.** `v1.0.0/data/postgres.md` (new "Event body storage" section).
- **Config.** None. If the measurement fails, close the slice with the numbers and move the small-row
  win to K3 (WS-7).

### S12. K1: superseding (latest-wins) event types

- **Changes.** An event type declares `[Superseding]` (new, `src/Whizbang.Core/Attributes/`); the
  generator discovers it as `EphemeralResolver` does (`src/Whizbang.Generators/Utilities/EphemeralResolver.cs:23`);
  dispatch stamps a new flag `Superseding = 1 << 5` (`EventFlags.cs:107` marks where new flags go),
  which the emit chain already carries into `wh_event_store.flags`. A new `perform_maintenance`
  task deletes the pointer and body of a superseding event once a newer event of the same type
  exists in the stream and the older one is fully consumed. Constraints:
  - **Full-state payloads only.** Each event must hold the whole state for its key. Rewind to before
    the latest event becomes impossible for that type; the rewind guards treat it like state-based
    (`EventFlags.cs:119`).
  - **Digests are decremented** the way `close_stream` does (`093_OriginGeneration.sql:196`).
  - **Reaper gates:** no pending perspective work, no cursor pointing at the event
    (`fk_perspective_cursors_event` is RESTRICT,
    `src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs:1445`),
    never the newest of its type in the stream, debug mode off.
- **RED first.** Generator: attribute discovered, flag emitted; an ephemeral type that is also
  superseding is a diagnostic. SQL, one test per gate: superseded and consumed is deleted with its
  body and digest contribution; pending work keeps it; a cursor keeps it; the newest is kept; a
  different type in the same stream is untouched.
- **Docs.** New `v1.0.0/fundamentals/events/superseding-events.md`; link from `event-store.md`.
- **Config.** None in the first cut.

## Verification

- Unit, SQL and generator suites per slice; coverage gate at 100% line and branch on new code.
- Before/after on a representative database: bytes per event (body, metadata), share of bodies
  compressed (`pg_column_size` against `octet_length`), audit bytes per record, digest equality
  between origin and subscriber after S1, S7 and S12.
- Expected: about 400 B saved per event from S9 (about 2 GB in the busiest service), audit bytes
  falling by the share the evidence attributes to bodies, ephemeral churn types leaving durable
  history after S2.

## Open questions

1. Default for `WriteForm`: `Slim` from 1.0 (smaller everywhere) or `Full` with opt-in (no behavior
   change)? Same for `AuditBodyDefault = Omit`, which changes what subscribers receive.
2. Audited ephemeral types: when the origin reaps the event, the audit pointer dangles. Keep the
   body for audited ephemeral types, or exclude ephemeral types from audit by default?
3. `OriginalStreamPosition` on the outbox path: nullable, filled at emit, or dropped from the
   record?
4. Keep the current hop's `sc` although it duplicates `wh_event_store.scope`, or rebuild scope from
   the column on read?
5. K1 key: latest-wins per (stream, type) only, or per (stream, type, key) with a key column stamped
   at emit?
6. K1 across services: a subscriber reaps on its own schedule, so origin and subscriber digests can
   disagree for a window. Accept and document, or reap only after the subscriber acknowledges?
