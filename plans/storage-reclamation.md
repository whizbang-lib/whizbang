# Storage reclamation

Umbrella plan for how Whizbang stops growing without bound and gives space back. Scope is
reclaiming space, not disaster recovery. Each workstream has its own plan file (see the index
below); this file holds the evidence, the decisions, the defects and the standards every slice
follows.

## Context

The evidence comes from a production deployment's development environment (PostgreSQL 17, four
environments on one server). Over one month the server's storage roughly doubled, and during a bulk
import the write-ahead log grew large enough that autogrow raised the disk to a size that cannot be
shrunk again. Backups grew with it.

Three things drive the growth, and none of them is the volume of business data:

- **Bytes the framework writes but nobody reads.** Envelope metadata is about 600 bytes per event,
  nearly all of it routing hops. Audit records copy the full body of the audited event, including
  embedding vectors and full document snapshots. High-frequency session/presence events and per-item
  saga progress events are stored as durable history although nothing replays them.
- **Bytes that are never compressed.** About 83% of event bodies are under the roughly 2 KB TOAST
  threshold, so the server's lz4 default never applies to them.
- **Tables that churn when they should not.** The event store, meant to be append-only, receives
  about 12 updates per inserted event and only 4% of those are HOT. Retention features that already
  ship are switched off, and 53 tables are queued for a rewrite that nothing performs.

## Evidence

Read-only queries, 2026-10-16 to 2026-10-18. Sizes are per environment database unless noted.

**Server**

| Measure | Value |
|---|---|
| Storage, one month apart | 128 GB, then 245 to 296 GB |
| Peak WAL during the import | 244 GB (`max_wal_size` is 25 GB, so something retained it) |
| Disk after autogrow | 512 GB, cannot shrink |
| Backup storage | 3.3 to 4.5 TB |
| Environment databases | 76.6 GB, 55.3 GB, 55.4 GB, 55.3 GB |

**Event store**

| Finding | Value |
|---|---|
| `wh_event_body` + `wh_event_store` share of each database | 50 to 57% |
| Bodies never compressed (row under the TOAST threshold) | about 83% |
| Metadata per event | about 600 B, nearly all `Hops` |
| Audit records' share of body bytes in the busiest service | 25% |
| Embedding events | each carries a full vector of about 24 KB; two types cover different entities, so they are not duplicates |

Bytes per hop, by field (JSON names from `src/Whizbang.Core/Observability/MessageHop.cs`):

| Field | Bytes | Note |
|---|---|---|
| `si` service instance | 125 | `required` on the type (`MessageHop.cs:93-94`) |
| `sc` scope | 101 | duplicates `wh_event_store.scope` |
| `md` hop metadata | 58 | |
| `tp` traceparent | 55 | |
| `co` correlation | 36 | |
| `ca` causation | 36 | |
| `ts` timestamp | 33 | |
| `to` topic | 20 | |

**Churn and bloat**

| Table | Finding |
|---|---|
| `wh_event_store` | about 22M updates against 1.8M inserts, 4% HOT; indexes twice the size of a fresh clone's |
| audit perspective table | 4M updates on 116k rows (about 35 per row), 0% HOT |
| two other update-heavy perspectives | about 0% HOT |
| `wh_perspective_cursors` | 52% HOT |
| `pending_table_rewrites` | 53 tables queued; `table_rewrite_policy=off` and `AllowTableRewrite=false` |

**Server configuration seen.** lz4 is already the default compression. `pg_repack`, `pg_squeeze` and
`pg_partman` are installed but not allowlisted; `pg_cron`, `vector` and `pg_trgm` are. A logical
replication slot has unbounded `max_slot_wal_keep_size`.

## Decisions log

Decisions made with the maintainer, 2026-10-16 to 2026-10-19.

| # | Topic | Decision |
|---|---|---|
| 1 | Scope | Reclaim space. Disaster recovery is out of scope. |
| 2 | Environment count | The four environments are intentional; consolidating them is not a lever. |
| 3 | A1 ephemeral classification | Yes. Mark churn types `[Ephemeral]`; the mechanism ships. |
| 4 | A2 audit | Yes, as a ledger with integrity: pointer + scope by default, body opt-in per type, one ledger stream per tenant, a lost ledger is re-derived from the origin's event store. |
| 5 | A3 metadata | Yes, a configurable metadata retention policy: slim at write, plus a reaper for history. |
| 6 | A4 large bodies | Superseding (latest-wins) event types (K1) in 1.0; blob claim-check for event bodies in 2.0. |
| 7 | A5 foreign copies | 2.0. Keep full integrity sync, so services keep their own copies in 1.0. |
| 8 | A6 compression | Yes. 1.0 lowers `toast_tuple_target` on `wh_event_body` after measuring; zstd with trained dictionaries goes into stream packs (K3, 2.0). |
| 9 | B1 end-of-life policies | Yes; wires `Disposition.*`, which today is always Delete. |
| 10 | B2 tier-2 pointer prune | Turn on after A1. |
| 11 | B3 archive store | `IArchiveStore` in 1.0, blob default with read-through. |
| 12 | B4 partitioning | Time-partitioned work ledgers in 1.0, managed by the maintenance worker; event store by `event_id` range in 2.0. |
| 13 | B5 ledger retention | Yes, including companion-row cleanup and log retention. |
| 14 | B6 close the books | Endorsed. `close_stream`, which truncates a prefix, is renamed so "close" means this. |
| 15 | C1 table rewrite | Two strategies behind one seam (VacuumFull, PgSqueeze); pg_repack is a runbook only. |
| 16 | C2, C3, C4 | Autovacuum tuning, fillfactor attributes, stop event-store churn; framework tables via migrations, perspective tables via generator attributes. |
| 17 | D5 telemetry | Yes; the 2.0 portal reads the same meters. |
| 18 | E extension capabilities | Catalog, startup probe and report, requirement metadata with fail-fast on explicit configuration. WS-4 and WS-5 depend on it. |
| 19 | P3 canonical-model compaction | Abandoned (below). |

## Abandoned: canonical-model compaction (P3)

The proposal was to compact an event stream by folding it into a canonical model and keeping only
the fold. It was stress-tested on 2026-10-18 and dropped, because every way of making it correct
turns into something already planned:

- **A stream has no model.** It is a sequence of heterogeneous state changes. Each perspective
  folds those events into its own model; the framework holds no single "state of this stream" to
  fold to. A perspective added later, or one rebuilt after a bug fix, cannot be built from another
  perspective's fold.
- **If the domain supplies the summary,** that is a domain-authored closing event followed by a
  gated truncate: close the books (B6, WS-3).
- **If the stream declares a canonical model,** that is the existing state-based path (the
  `Compacted` flag, `src/Whizbang.Core/Messaging/EventFlags.cs:105`), already used by ephemeral
  tier 2.
- **Otherwise the only model-agnostic reduction is relocation:** archive the raw events and
  truncate the hot store (B3, WS-3).

The reasoning is recorded in the docs repo at `src/assets/docs/proposals/archival-compaction.md`
("a stream has no model").

## Workstream index

| WS | Plan | Contents | Release | Depends on |
|---|---|---|---|---|
| WS-2 (#1493) | [storage-event-slimming.md](storage-event-slimming.md) | A1 ephemeral guidance, A2 pointer audit + ledger, A3 metadata slimming + retention, A6 toast target, K1 superseding types | 1.0 | WS-6 for before/after numbers; WS-4 to rewrite existing rows after A6 |
| WS-3 (#1494) | [storage-stream-end-of-life.md](storage-stream-end-of-life.md) | P1 finalize/seal, B1 policies + `Disposition`, B3 `IArchiveStore`, B6 close the books, `close_stream` rename | 1.0 | none |
| WS-4 (#1495) | [storage-bloat.md](storage-bloat.md) | C1 rewriter seam, C2 autovacuum, C3 fillfactor + event-store churn, C4 | 1.0 | WS-8 (pg_squeeze) |
| WS-5 (#1496) | [storage-ledger-retention.md](storage-ledger-retention.md) | B5 companion cleanup, time-partitioned work ledgers, log retention, settings config path | 1.0 | WS-8 (optional pg_partman) |
| WS-6 (#1497) | [storage-telemetry.md](storage-telemetry.md) | D5 table meters for every `wh_` table, bytes per event type, dashboard | 1.0 (portal 2.0) | none |
| WS-8 (#1498) | [storage-extension-capabilities.md](storage-extension-capabilities.md) | E catalog, probe, report, requirement metadata, options validation | 1.0 | none |
| WS-7 (#1499) | [storage-post-1.0.md](storage-post-1.0.md) | A4 claim-check, A5 foreign-copy pointers, event-store partitioning, K2 dedupe, K3 stream packs | 2.0 | WS-2, WS-3 |

Operator tuning of the settings that already ship (retention windows, maintenance cadence, turning
on reapers) is deployment work, not framework work, and is not tracked here.

## Defects found

Each becomes a GitHub issue on the 1.0 board. Issue numbers are filled in when filed.

| # | Defect | Fixed in | Issue |
|---|---|---|---|
| 1 | Row TTL and row-cap sweeps delete the perspective row but orphan its `wh_perspective_cursors` and `wh_perspective_snapshots` rows; a stale snapshot can restore evicted state on rewind. | WS-5 | #1483 |
| 2 | The `deliveryGuarantee` filter never matches: digest folds and integrity reads look in `wh_event_body.metadata`, which the emit chain never writes it to, so at-most-once events are probably counted (inferred, untested on the real write path). | WS-2 | #1484 |
| 3 | `EventAudited`: `CausationId` never set; `OriginalStreamPosition = 0` on the outbox path; two of three writers leave `OriginalEventId` empty; the outbox path skips `IAuditDecisionHook`; the per-tenant ledger stream was decided but never shipped. | WS-2 | #1485 |
| 4 | `close_stream`: the cursor foreign key is RESTRICT, so a close fails when a cursor points into the truncated range; stale snapshots below the close point remain. | WS-3 | #1486 |
| 5 | Most retention `wh_settings` keys have no configuration path, only SQL. | WS-5 | #1487 |
| 6 | The table statistics meter covers 7 hard-coded tables. | WS-6 | #1488 |
| 7 | `wh_event_store` takes about 12 updates per event at 4% HOT on a table meant to be append-only (to investigate). | WS-4 | #1489 |
| 8 | `WorkCoordinator:MaintenanceInterval` was removed with no warning; old configurations keep it silently. | WS-8 | #1490 |
| 9 | The idle-gate ceiling is not a time: it is `MaxConsecutiveDeferrals x IntervalMinutes`, so changing the tick silently changes it. | WS-4 | #1491 |
| 10 | A composite-fanned `EventAudited` folds into the origin's digest lane on the subscriber, where the origin has no matching rows (inferred). | WS-2 | #1492 |

Defects 8 and 9 have no natural home in the source discussion; they are placed with the
workstreams that already touch options validation (WS-8) and maintenance scheduling (WS-4).

## Engineering standards for every slice

- **Strict red before green.** Before writing a test, enumerate the method's exits (each return,
  throw, early-out and branch outcome), then write one failing test per exit and watch it fail for
  the right reason. See `ai-docs/tdd-strict.md`.
- **100% line and 100% branch coverage** on new and changed code. Exclusions follow
  `ai-docs/coverage-exclusions.md` and are member-level and justified.
- **AOT compatible.** No reflection in runtime projects. New options bind through the
  configuration binder source generator from a literal section string
  (`configuration.GetSection("Whizbang:...")` or `ProcessWideOptionsBinding.Section(services,
  "Whizbang:...")`, `src/Whizbang.Core/Configuration/ProcessWideOptionsBinding.cs:47`); never
  `BindConfiguration`. Attribute discovery happens in generators.
- **Code links to docs and tests.** Public types and members carry `<docs>` (versionless path) and
  `<tests>` XML tags; see `ai-docs/documentation-maintenance.md`.
- **A docs page per feature** in the docs repo (`whizbang-lib.github.io`, under
  `src/assets/docs/`), shipped in the same change as the code it describes. A published key that
  nothing reads is a defect.
- **SQL follows the migration rules** in `src/Whizbang.Data.Postgres/Migrations/README.md` (13
  rules): idempotent, `__SCHEMA__` qualified inside function bodies, redefine a function whole,
  zero-padded unique prefix (the highest today is 210), shared literals as tokens in
  `constants.txt`, and `<docs>`/`<tests>` comments above every function (rule 13). Run
  `scripts/Lint-MigrationSql.ps1`. Hot-path SQL is measured on a copied table first.
- **Signed-off commits** (`git commit -s`, DCO gate).
- **Generic language.** No consumer names, environments, hostnames or consumer event types in code,
  tests, docs, commits or plans.

## Verification (whole program)

Each workstream defines its own checks. Program-level: re-run the read-only size, bytes-per-event
and HOT-ratio queries against a representative database before and after each workstream lands,
and read the same numbers from the WS-6 meters once they ship.
