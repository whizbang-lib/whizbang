# Plan: storage cleanup and stream integrity work as one system

Status: APPROVED by the owner (2026-11-15); all open questions answered (section 8). Issues: #2342 (matrix and conflicts), #2343 (rules library and pair guard). ADO 21076.
Evidence: architecture survey of develop @ 09ad49108 (file:line references below), plus the #2342 cross-mechanism test matrix (RED, in progress).

## 1. Context

Two subsystems are each well built and tested, but they were never designed together.

- **Cleanup** deletes or rewrites events. There are 16 paths (section 3A). Each decides locally, gated by local timing, and subtracts what it removed from the integrity digest at the moment it deletes.
- **Integrity** compares digests across services. It repairs a *deficit* by redelivery and alarms on a *surplus*.

So a deliberate removal on one side looks like damage to the other:
- a consumer that removes history the origin still holds looks short, and gets redelivered;
- an origin that removes history the consumer still holds leaves the consumer with a surplus that alarms forever.

The owner's question was: "are we going to clean up data and the integrity engine is going to just resync it again?" The answer today is yes, in several paths (section 3F).

Decisions already made (#2342 comments):
1. A consumer may keep less than the origin, under its own policy. Erasure is the exception: the origin decides it and propagates it.
2. The design leans into sealed epochs. The digest follows *eligibility*, never *timing*.
3. Rule versions are compared per service pair. A mismatch reports an error and integrity does not run for that pair. The rules live in their own versioned library.
4. Sealing is on by default and has its own configuration. The seal re-check and the full-history compare are opt-in "suspenders", off by default, and sized by DevOps.

Constraint: the cost of "I no longer keep this" may grow with types and with streams that were cut, never with events.

## 2. Target architecture

### 2.1 Principles
- **P1. Eligibility vs timing.** *Eligibility* asks whether an event should exist under the declared rules. It is a pure function of the event's own data (type, flags, key, version) and the rules. *Timing* asks when this service may physically delete it (consumed, stamped, no snapshot, no hold, idle gate). Timing is local and differs between services, and that is fine.
- **P2. The digest is a fold over eligible events,** not over the rows physically present. Physical deletion never changes a digest, never refolds an epoch, never lowers a seal and never bumps the generation. Only a change to the rules or a cut (section 2.3) changes what is counted.
- **P3. One home.** One definition of eligibility, used by every digest fold, answer, heal, checkpoint and cleanup path. One deletion helper per kind of removal. Guard tests fail the build on a copy.
- **P4. Shape-specific handling.** Rule-shaped cleanup (ephemeral, at-most-once, audit, superseded copies) goes through eligibility. Cut-shaped cleanup (truncate, archive, purge, end-of-life) goes through a carried digest (section 2.3). Erasure is origin-propagated.
- **P5. Agreement first.** Two services compare only when they run the same rules for the types they share (section 2.5).
- **P6. Bounded cost.** Steady-state integrity costs track the open window, never total history. The checks that grow with history are opt-in and sized by DevOps.
- **P7. Observable.** Every decision above has a signal (section 2.7). No per-stream or per-tenant metric attributes.

### 2.2 One eligibility definition (fixes section 3B)
Today the digest predicate is hand-copied into 15 SQL and C# sites, and 5 of them differ:
- the seed path has no flag-32 test and no exempt-type filter;
- the consumer-side checkpoint count has no flag-32 test;
- the reclassify, at-most-once, archive and purge unfolds have no exempt-type filter.

Change:
- Define the predicate once as a migration-time constant (`__DIGEST_ELIGIBLE__(alias)`), substituted into every SQL site the way `__DIGEST_EXEMPT_TYPES__` already is. This costs nothing at runtime and keeps the plans the planner already chooses (the lesson from migration 446: never hide filters behind an opaque function).
- C# reads the same constant through the generated SQL constants, never its own literal.
- A guard test, in the style of `StreamDigestFoldGuardTests`, fails on any digest-context use of `flags & 8`, `flags & 32` or the exempt list outside the constant.

### 2.3 Cut-shaped cleanup carries its digest (fixes section 3F items 2 and 3)

#### Option A (recommended): carry
When a service truncates, archives or purges, the digest contribution of what it removed is *moved* into the stream's lifecycle row (`carried_lo/hi/count/ilo/ihi`) instead of being subtracted. The comparable digest is the live fold plus the carried part, which is exactly what it was before the cut.

What follows from that:
- Epochs are not refolded, seals do not move and the generation is not bumped.
- No wire change is needed: the origin and the consumer cut independently, and neither looks short or long to the other.
- Repair never requests below the stream's own floor (`truncated_through_version`, already in `wh_stream_lifecycle`, 297:36). Today only the end-of-life step writes the floor, and the plain truncate (A8) does not; that is fixed here.
- A whole purge keeps a tombstone lifecycle row with its carried digest. The archive already keeps one.

Storage is one row per cut stream. Once the epoch covering a carried stream is sealed, carried rows can optionally be compacted into one carried total per (lane, tenant, type). That brings the cost down to one row per type, at the cost of no longer naming streams in a drill-down below the floor.

#### Option B: send floors
The consumer sends its per-stream floors on manifest requests, and the origin answers above them. That needs a wire change, an origin-side recompute per cut stream, and an O(cut streams) payload on every request.

Recommendation: A. The open question is whether erasure should still carry; see Q4.

### 2.4 Superseding types count the key, not the copy (fixes section 3F item 1)
Today an older copy's digest contribution is subtracted when the reaper runs. That is timing, so whichever side reaps first looks short. Making "subtract when a newer copy arrives" the trigger instead would refold the sealed epoch holding the old copy on every update, which is the #2262 seal storm again.

#### Option A (recommended): count the key
For a type declared `[Superseding]`, the digest folds a stable *slot identity* (stream, group, key) once, whichever copy is present. Arrival of each copy is covered by the continuity checkpoints in the open window, the same way ephemeral events are covered today.

What follows from that:
- The reaper deletes physically and never touches the digest.
- Reap order across services is irrelevant.
- Seals never move on an update.

What it gives up: after the window, a service that holds an older copy and lost the newest one is not detected by the digest. A whole key lost is still detected.

#### Option B: count the newest copy
Count only the newest copy (fold the newest event's id). The digest stays exact, but seals and epochs churn on every update.

Recommendation: A. Superseding is declared as "only the newest matters", and checkpoints already guard the window.

### 2.5 Rule fingerprint and pair guard (#2343)
- **Fingerprint** per service pair, over the types they share (consumed by one and owned by the other), so a rule change on an unrelated type halts nothing. It hashes three things:
  - the rule fields of each shared type's catalog entry (Ephemeral, Superseding plus group and key, Terminal, end-of-life policy, exemption);
  - the rules library's semantics version;
  - the SQL eligibility constant's version.

  Reuse `MessageTypeCatalogEntry.SettingsHash` (`Core/IMessageTypeCatalog.cs`) where its inputs match. End-of-life policies synced from configuration count too.
- **On the wire:** add `RuleFingerprint` (and the type list it covers) to `RequestIntegrityManifest`, `IntegrityManifest`, `IntegrityCheckpoint` and `RequestRedeliveryCommand`. A missing field is treated as "legacy". On an upgrade boundary, legacy pairs keep the current behavior until both sides send a fingerprint.
- **Guard:** on a mismatch, each side logs an error naming the pair and the differing types, records the pair as halted, and does not compare, repair or redeliver for that pair. It resumes on its own when the fingerprints match. When a rule change takes effect, a one-time refold of the affected types follows, with a generation bump floored at their first position.
- **Rules library:** a `Whizbang.Rules` package holding the eligibility semantics, the catalog rule model, the fingerprint and the SQL constant's source. It gets its own version track (section 2.8).

### 2.6 Sealing, re-check and full-history compare (decision 4)

| Section (`Whizbang:StreamIntegrity`) | Setting | Default | Note |
|---|---|---|---|
| `Sealing` | Enabled | on | close and seal epochs |
| | EpochWidth | 100000 | today only the wh_settings key `integrity_epoch_width`; it gains a config path. Pinned per lane at first close |
| | MaxClosuresPerCycle | 64 | replaces `MaxEpochClosuresPerMaintenanceCycle`, with the old key kept as an alias |
| `SealRecheck` | Enabled | **off** | local recompute of sealed epochs (today `verify_digest_epochs`, run on every sweep) |
| | Schedule | `0 3 * * *` | per-service splay, as today |
| | Lookback | 30.00:00:00 | epochs sealed within the lookback |
| | RecheckEvery | 7.00:00:00 | rotation: each epoch at most once per interval |
| | MaxEpochsPerRun | 10000 | |
| | OnDrift | Refold | or ReportOnly |
| `FullHistoryCompare` | Enabled | **off** | the sweep's heal over the whole store and its full-history ask (today every 7th audit or the cron, on by default) |
| | Schedule, Lookback, MaxStreamsPerRun | | |

Fixes found by the survey:
- `verify_digest_epochs` restarts at epoch 0 on every call with no cursor (092:~174-180). Past 10k epochs, later epochs are never verified, which contradicts the option's own documentation. It needs a persisted rotation cursor and a `sealed_at` column on `wh_digest_epochs` (one per epoch).
- The routine (non-sweep) stored-digest heal stays on. It is bounded by the window.
- Upgrade note: turning both re-checks off by default is a behavior change. It goes in the changelog Upgrade note and the startup settings report.

### 2.7 Observability (section 3G)
Existing meter names are kept. New detail comes as new instruments or attributes, and any rename gets a deprecation period.

New instruments (meter `Whizbang.StreamIntegrity` unless noted):
- **Resync loop:** `redelivered_then_reaped_total` (target 0), and `redelivery_refused_total{reason=below_floor|ineligible|pair_halted}`.
- **Pair guard:**
  - `pair_state{origin,state}` and `pair_halted_seconds{origin}`;
  - `rule_fingerprint_info{fingerprint}`, an info gauge. Its cardinality is the number of rule versions in the fleet.
- **Seals:**
  - `epoch_frontier{origin}`;
  - `open_window_events{origin}`;
  - `seal_age_seconds{origin}`;
  - `seal_lowered_total{reason=rule_change|rewrite}`;
  - `origin_generation`.
- **Re-checks:**
  - `recheck_enabled{check}`;
  - `recheck_last_run_timestamp{check}`;
  - `recheck_coverage_ratio{check}` (the share of the lookback re-checked within RecheckEvery);
  - `recheck_epochs_checked_total`;
  - `recheck_drift_total`;
  - `recheck_duration`.
- **Cuts:** `carried_streams` and `carried_compactions_total`.
- **Cleanup lag** (meter `Whizbang.Maintenance`): `pending_rows{task}` extended to the body reap, superseded reaper, end-of-life and truncate (it does not count those today), plus `eligible_not_yet_deleted{task}`.

Traces:
- A new `Whizbang.Integrity` ActivitySource, with spans for each maintenance task, audit comparison, sweep, re-check, repair drain request and redelivery build.
- `RequestRedeliveryCommand` and the redelivery composite get a request id and carry trace context, so one trace runs from a gap found to the repair stored, across services. None of this exists today.

Dashboards and alerts:
- Ship `dashboards/grafana/whizbang-integrity.json` (none exists today) and extend the storage dashboard with cleanup lag.
- Ship Prometheus alert rules as a file:
  - resync counter above 0;
  - a pair halted longer than a threshold;
  - the frontier or seal not advancing;
  - the open window growing;
  - re-check drift above 0;
  - cleanup lag growing;
  - a re-check disabled while its coverage is expected.

### 2.8 Packaging (rules library version track)
Today one GitVersion version packs all 31 packages, and `verify-packages` requires exactly one version (`reusable-pack.yml:79`, `.github/actions/verify-packages`, `.github/nuget-packages.txt`).

Change:
- `Whizbang.Rules` gets its own version, computed from changes under its directory, with its own tag prefix (`rules-v`).
- The other packages depend on it by version range.
- `verify-packages` and the package list learn two version tracks.
- A guard fails the build when the rule-semantics golden file changes without a version bump.
- The consumer feed copy after each alpha copies one more package.

Board only, no ADO (release pipeline rule).

### 2.9 Operational data has a rolling retention (decision 5)
Everything cleanup and integrity record about their own work (ledgers, logs, queues, attempt and delivery records, drift reports, halt history, re-check results) is kept for a configurable resting period, so it can be investigated, and is then removed. Their bookkeeping must not grow with the system. Only state that correctness depends on is kept indefinitely.

- **Two classes, every table in exactly one:**
  - **State (kept).** Epoch seals (`wh_digest_epochs`), consumer seals (`wh_integrity_seals`), lane frontiers, stream lifecycle rows with their floors and carried digests, *current* pair state, rotation cursors and current generation.
    - Each is bounded by lanes, types, epochs or cut streams, never by events or by time running.
    - Carried rows compact per Q4.
  - **Rolling (expires).** Closed ledger rows and repair attempts; the rewrite log `wh_integrity_origin_rewrites` (today capped at 10,000 rows by count); delivery and answer records; resolved gap-tracker entries; drift and divergence reports; pair halt history; re-check run results; finished queue rows (superseded queue, pending rewrites, reclamation work); and any per-run maintenance record.
- **Configuration:**
  - `Whizbang:OperationalRetention:Default` (suggested 14 days), plus a per-category override under `Whizbang:OperationalRetention:<Category>` (for example `RepairLedger`, `RewriteLog`, `Deliveries`, `Reports`, `PairHistory`, `RecheckRuns`, `Queues`).
  - Each category can also take an optional row cap as a hard ceiling.
- **Safety:**
  - A rolling row is removed only when it is past retention *and* closed. An open ledger bucket, an in-flight delivery or an unanswered request is state until it resolves.
  - A row something still reads (for example the rewrite log entry a consumer's seal guard may still need) carries a floor: the rewrite log keeps every entry newer than the oldest consumer seal generation it can still be asked about, and removes the rest by time.
- **Execution:** one maintenance task sweeps every rolling category in bounded batches inside the existing maintenance pass and idle gate, so it never competes with live work. It is not a separate worker.
- **Gravity:**
  - A registry classifies every `wh_` table as State (with the reason it is bounded) or Rolling (with its category).
  - A guard test fails the build when a migration creates a table that is in neither, so a new operational table cannot ship without a retention rule.
- **Observability:** `operational_rows{category}`, `operational_oldest_age_seconds{category}` and `operational_rows_expired_total{category}`, plus an alert when the oldest age exceeds 2x the configured retention, which means the sweep is not keeping up.
- **Out of scope:** telemetry itself (logs, traces, metrics). Its retention belongs to the observability backend, not W!.

### 2.10 Performance is a design input, measured and budgeted (decision 6)
**Evidence** (dev server, 8 vCores, one active environment, measured 2026-11-13 to 15):
- **CPU at rest is about 12%,** against 5-6% before integrity. The jump starts on Nov 3.
- **WAL went from about 2.6 GB/day to 30-206 GB/day.**
- **About half the CPU does not appear in statement statistics.** Sampling found it: statement time excludes parallel workers, and the parallel workers of the sequence-less fold in the type-level integrity answer (#2351, about 282 ms and 32k blocks per call) account for it. Autovacuum and the logical-replication consumer are not significant (a 20-minute pause of the consumer changed nothing measurable).
- **Statement-time share:**
  - claiming work, 36%;
  - tiny pollers, 26%;
  - integrity digest compares, 13%;
  - message store and commit, 8%;
  - leases and roles, 7%;
  - the table-statistics probe, 5%.
- **469 statements/s at idle,** of which:
  - DISCARD ALL, 155/s;
  - the CDC poll, 108/s;
  - pollers, about 90/s;
  - checkpoint traffic, 1.41 messages/s;
  - rebuild fan-out, 0.44 messages/s, 94% of it wasted.

**Found by the hour of background sampling (2026-11-15):**
- **Hidden CPU.** The "missing half" of CPU is mostly ours: the parallel workers of one integrity query, the by-type fold of received events with no origin sequence (#2351). About 2.9M such rows sit on one service; the query costs about 282 ms and 250 MB of buffer reads per call, about 47k calls.
- **Not the CDC.** The CDC walsender waited on its client in 238 of 240 samples, so it is not the cause.
- **WAL.** About 95 GB/day at idle. The top writers are inbox storage, claiming and outbox, which is mostly idle control traffic, so #2344 and #2348 cut WAL as well as CPU. Checkpoints run every 10 minutes and wrote about 1.8 GB in 72 minutes.

**Principles:**
- **Cost scales with work, never with time, services, or the size of the history.** Every mechanism states its cost as one of:
  - per event written;
  - per change found;
  - per open window;
  - a bounded idle constant per service.

  "Per tick", "per row stored" and "per epoch ever sealed" are not acceptable steady-state costs.
- **Idle is nearly free.** Pollers back off (#2346), checkpoints go out on change with a slow heartbeat (#2348), comparisons are skipped when nothing moved (#2349), commands reach only their owner (#2344), and the pooler reset goes (#2347).
- **WAL is budgeted.** Every cleanup and integrity write path states its WAL per event or per run. The "eligibility, not timing" change (2.1 P2) already removes the digest, epoch, seal and generation writes on every physical deletion. Cleanup batches stay bounded, so checkpoints and full-page writes stay small.
- **Claims cost time in proportion to what they claim** (#2345).

**Measurement built in:**
- **Attribution.** Every W! background mechanism tags its database session (`application_name`, for example `whizbang:maintenance:superseded_reap` or `whizbang:integrity:manifest_compare`). `pg_stat_activity`, Query Store and the server's own tooling can then attribute CPU and I/O by mechanism without guessing. This is also what tells us whether the untracked half is ours.
- **Metrics.**
  - Per mechanism: `whizbang.db.statements{mechanism}`, `whizbang.db.duration{mechanism}` and `whizbang.db.rows{mechanism}`. The mechanism is a bounded set.
  - Idle traffic: `whizbang.messages.idle_control{type}`.
  - WAL per run: `whizbang.maintenance.wal_bytes{task}` (from `pg_current_wal_lsn` before and after; cheap).
- **Plan-shape tests** (the migration 446 pattern) for every hot cleanup, integrity or claim statement.
- **A scaling test.** The same operation runs against a small and a large store, and the test fails if the cost grows by more than a set factor. That is how "does not grow with history" becomes enforced instead of hoped for.
- **A dev before-and-after for every phase,** recorded in its PR: CPU floor, WAL/day, idle statements/s and messages/s, using the same read-only queries as this investigation.
- **Targets.** Return the dev CPU floor to the pre-integrity 5-6% and WAL to single-digit GB/day at idle.

**Delivery:** an "idle cost" bundle (#2344 to #2349) runs in parallel with phases 1 and 2. Attribution tagging and the per-mechanism metrics land first in that bundle, so every later phase is measured from the start.

## 3. Current pieces (survey summary)
Full report: `inventory-2342-architecture.md` (scratchpad); appendix to be committed with this plan.
- **A. Cleanup paths:** A1 body reap, A2 pointer prune, A3 reclassify, A4 bookkeeping (#2320), A5 migration 210 rewind removal, A6 superseded reaper (no origin filter), A7 superseding reconcile, A8 truncate, A9 close the books, A10 archive, A11 restore, A12 purge, A13 at-most-once flagging, A14 end-of-life, A15 crypto-shred (not shipped), A16 re-attribution, refile, adoption and body-only rewrites.
- **B.** 15 predicate copies, 5 divergent.
- **C.** The whole-store heal on every sweep and the backstop's restart-at-0 are the history-growing costs.
- **D. Reuse:**
  - the lifecycle row, which already holds `truncated_through_version`;
  - the generation and rewrite floor (migration 408);
  - the manifest generation fields;
  - the catalog `SettingsHash`;
  - the consumed-type registry and domain claims.
- **F. Conflicts:**
  1. Consumer superseded reap leads to deficit, redelivery, re-store and re-reap.
  2. An origin cut leaves the consumer a permanent `local_extra`.
  3. A consumer cut leads to a deficit, then a refused or re-stored repair.
  4. Migration 210 deleted rewind events without touching digests (the likely source of the `drift_removed` warnings), and the seed path folds audit records.
  5. Predicate divergence.
  6. One generation per database, not per type.
  7. No channel for "I keep less".
  8. Backstop coverage gap.
  9. No rule version anywhere.

## 3b. Proof: the #2342 matrix (RED, commit b206ba528 on fix/cleanup-integrity-matrix-2342)
**Setup:** 242 cases across two real stores, run through the production receive path (redelivery reconcile and durable ledger). Each case runs one cleanup path on one side against one audit variant.
- **194 pass and 48 fail.** Every failure is a genuine conflict: each case's positive control was found and repaired, and a precondition confirms the cleanup actually changed the store.
- The last two runs gave identical results.
- Tally per audit cycle: repair requests / divergence reports / events redelivered / events stored again.

| Group | Cleanup (side) | Behavior | Cases | Fixed by |
|---|---|---|---|---|
| Loop: data returns | purge (consumer) | 1/1/4/4 every cycle; purge also clears dedup, so nothing stops the re-store | 4 | phase 4 (carry, seal-gated) |
| Loop: wasted forever | archive whole (consumer) | 1/1/4/0 every cycle; refused at the inbox, never marked unhealable | 4 | phase 4 |
| Loop | reclassify (consumer, windowed) | repeats every cycle | 1 | phase 3 (rule change under the pair guard) |
| Alarm forever | superseded reap, prefix truncate, prefix archive (origin) | `local_extra` every cycle; the compare has no per-stream floor although the docs say it has | 16 | phase 4 (symmetric carry); docs corrected |
| One wasted resend, then unhealable | prefix truncate, close the books, prefix archive (consumer); reclassify (consumer, 3 audits) | 1/1/4/0 then quiet, with a permanent unhealable ledger row | 15 | phase 4; phase 3 |
| Wrong data, permanent | superseded reap (consumer) | both older copies restored (2/2/2/2). Redelivery carries no child metadata (`CompositeInboxFanout.cs:421-428`), so they lose their superseding key and land at a **higher local version than the newest copy**. They are never reaped again, and "newest" is now the stale copy | 6 | phase 5, plus a new item: redelivery carries the superseding flag and key and keeps the origin's version order. **This is a correctness bug, not only a cost bug: ship it early, in phase 1b or as its own fix** |
| Digest follows timing | one-sided superseded reap (heal and redelivery) | the side that has not reaped still counts its superseded copies | in the above | phase 5 (key slot) |

**Green:** every case where both sides clean up, the ephemeral body reap and pointer prune, at-most-once, audit records, archive then restore, and the origin-side whole archive, purge and reclassify.

**Not reproduced:** rewind bookkeeping drift. The only stale-bucket path is migration 210's one-time removal; phase 1 cleans up its leftovers.

**Not covered:**
- Dapper, which has no integrity path (the shared cleanup SQL is covered through EF).
- Continuity checkpoints, subscription backfill and the coverage audit, argued N/A.
- The paced drain (the matrix uses the inline repair path, which admits the same deficits).
- Historical at-most-once flagging on the origin.

These become follow-up cases as their phases land.

**Acceptance:** each phase turns its rows green, and the matrix's digest-follows-eligibility invariant becomes the plan's overall acceptance test.

## 4. What changes, component by component

| Component | Change | Stays | Retired |
|---|---|---|---|
| Emit chain folds (outbox, inbox, direct append) | use `__DIGEST_ELIGIBLE__`; superseding types fold the slot identity | single fold helper `_wh_fold_stream_digests` | per-site literals |
| Superseded reaper (A6) | stops unfolding, refolding and bumping | gates, locks, ordering | digest subtraction |
| Truncate, archive, purge, end-of-life (A8, A10, A12, A14) | carry instead of subtract; A8 writes the floor; purge keeps a tombstone | the rest | unfold, refold and bump on cut |
| Reclassify, at-most-once flagging (A3, A13) | treated as a rule change: one refold plus a floored bump; exempt filter fixed | | |
| Seed (`wh_seed_stream_digests_batch`) | uses the shared predicate | | divergent predicate |
| Heal and stream recompute | adds the carried digest; slot identity for superseding types | linear plan (446) | |
| Checkpoints | consumer count adds the flag-32 test; carry the fingerprint | | |
| Manifests, requests, redelivery | fingerprint; request id and trace context; no request below the floor | | |
| Pair guard | new | | |
| Seal backstop | rotation cursor, `sealed_at`, own config, off by default | | restart-at-0 |
| Full sweep heal and full-history ask | own config, off by default | the routine windowed heal | |
| Options | `Sealing`, `SealRecheck`, `FullHistoryCompare` sections | old keys as aliases for one release | |
| Telemetry | section 2.7 | all existing instruments | |
| Packaging | rules library track | | single-version assumption |

## 5. Upgrade and existing data
- **Digests change meaning** in phases 4 and 5 (carry, slot identity). Each service's upgrade migration recomputes its stored digests and epochs once. The heal is linear now (446). That bumps the generation with floor 0, so consumers re-verify once, from epochs, cheaply.
- **Order matters:** the pair guard (phase 3) ships *before* any change in digest meaning. During a rolling deploy, a pair of old and new services is then halted instead of comparing digests computed two ways and repairing "losses" that are not real.
- **Leftovers from migration 210:** digest rows with no events are removed once by the heal, which already happens. The plan adds a one-time cleanup so the warnings stop everywhere, not only on services that sweep.

## 6. Delivery (PRs, each with tests, docs, board, ADO, then the consumer adoption drill)
0. **#2342 RED matrix and inventory** (in progress). It stays on its branch and goes GREEN case by case as the phases land. Its invariant test is the acceptance test for the whole plan.
1. **One predicate** (#2352): the constant, the 5 divergent sites, the guard test, and the one-time cleanup of migration-210 leftovers. Small. Fixes F4 and F5.
2. **Sealing and re-check configuration** (#2353): the three sections, the backstop rotation and `sealed_at`, re-checks off by default, and their metrics. Fixes F8. Independent of the rest.
3. **Fingerprint and pair guard** (#2343, the protocol part): computed in place first, before the library split, plus pair metrics and the guard. Must land before 4 and 5.
4. **Cuts carry:** carried digest in the lifecycle row, the floor written by truncate, no repair below the floor, tombstones, carried metrics. Fixes F2, F3 and F7.
5. **Superseding slot identity** (with #2350): the reaper stops touching digests, plus the one-time recompute. Fixes F1. Gated on Q1.
6. **Observability completion:** the `Whizbang.Integrity` spans, the request id and trace link, the integrity dashboard and the alert rules file.
1b. **Idle cost and measurement** (2.10): session attribution and per-mechanism metrics first, then #2351 sequenceless fold (largest single win), #2344 rebuild routing, #2345 claim cost, #2346 idle pollers, #2347 pooler reset, #2348 checkpoints on change, and #2349 comparison and probe cadence. Before-and-after numbers on dev. Parallel with 1 and 2.
2b. **Operational retention** (2.9, #2354): the table registry and its guard test, the rolling sweep task, configuration and metrics. The first step is an inventory of every cleanup and integrity table and how it is bounded today. Independent of the rest; can run in parallel with 1 and 2. Every later phase that adds a table classifies it here.
7. **Rules library and version track** (#2343, packaging): move the rule model, fingerprint and constant source into `Whizbang.Rules`, plus the pipeline work. Board only.

Phases 1 and 2 can run in parallel. 3 comes before 4 and 5. 6 rides along incrementally, with the dashboard and alerts last. 7 can follow 3 at any time.

## 6b. Bundles (agreed with the owner, 2026-11-15: three pull requests in all)
Each bundle is one pull request (plus its docs pull request), closing every issue it lists.

| Bundle | Phases | Issues |
|---|---|---|
| 1. Settings and retention | 2, 2b | #2353 sealing and re-check configuration, #2354 operational retention |
| 2. Cost and correctness groundwork | 1, 1b, follow-ups | #2351 held-without-sequence fold cost, #2352 one counting rule, #2350 redelivered copies keep their key, #2344 to #2349 idle cost, #2359 unbounded tables, #2361 full-history compare limits, #2360 question 2 once decided |
| 3. One system | 3, 4, 5, 0, 6, 7 | #2362 pair guard, #2363 cuts carry, #2364 superseding key slot, #2342 the matrix fully green, #2365 traces, dashboard and alerts, #2343 rules library and version track |

Bundle 3 changes what a digest means, so within it the pair guard must treat a peer with no fingerprint (a service on an older release) as a mismatch: a rolling deploy then halts mixed pairs instead of comparing digests computed two ways. Bundle 3 starts once bundle 2's shared counting rule (#2352) is on its branch base. Each bundle records its dev before-and-after numbers (section 2.10) in its pull request.

## 7. Risks
- **Slot-identity detection gap** (2.4): an older copy remaining while the newest is lost goes undetected after the window. Mitigation: checkpoints cover the window, and the window is longer than redelivery.
- **Carried digests cannot be re-verified,** because their events are gone. They are trusted like sealed epochs, by design.
- **Pairs halted during long rollouts:** they are visible on the dashboard, and the alert fires only past a threshold. Integrity is paused, not wrong.
- **Planner regressions** from a broader predicate in the heal and answers: plan-shape tests in the style of 446 are kept for every touched statement.
- **Crypto-shred is not shipped** (A15). Erasure must stay origin-propagated when it lands; this plan only reserves that path.

## 8a. Weighing Q2 (how cut-shaped cleanup stays invisible) and Q4 (what is kept for removed history)

### Key facts that shape both
- Type-level answers come from **sealed epochs plus a live fold of the open window** (section 3C). If a cut does not refold epochs, sealed answers already include the removed events and do not change.
- Stream-level detail (drill-down, the stored-digest heal, the seal re-check) is what notices a hole.
- Digest buckets are already per (lane, tenant, type, stream), and epoch buckets per (lane, tenant, type, epoch). Both rows **already exist**.
- Repair below a truncate point is already refused (migration 252).

### Q2 options

| | A. Carry (in the existing bucket) | B. Send floors | C. Carry, gated on the seal |
|---|---|---|---|
| How | A cut moves the removed contribution into `carried_*` columns on the stream's digest bucket row; the comparable digest is live plus carried | the consumer sends its per-stream floors, and the origin answers above them | A, plus a cut may remove only history **below this service's sealed frontier** for that lane; a cut asked for above it waits (a local timing gate) |
| Wire change | none | floors on requests (O(cut streams) per request), and floors on answers too, for origin-side cuts | none; optionally the consumer reports its `sealed_through` on requests (one value) |
| Steady CPU | none: no refold, no seal move, no generation bump | origin recompute per floored stream **on every audit**, so it grows with the number of cuts over time (fails 2.10) | same as A |
| Symmetry (origin cuts, consumer cuts, both) | yes: every side keeps "everything ever", so nobody looks short or long | needs floors in both directions | yes |
| Verification | the carried part is trusted, not re-checkable (its events are gone) | everything compared is real data | trusted, but only ever for history **already verified** before it was removed |
| Rolling upgrade | needs the pair guard (phase 3), since digest meaning changes | protocol version skew across services | as A |
| Rule change after a cut | the bucket is per type, so making a type ineligible drops its bucket, carried part included | origin recomputes | as A |
| Failure mode | a bug in carrying is permanent drift | a stale floor leads to wrong comparisons | as A, but limited to verified history |
| Complexity | low | high | low plus one gate |

**Recommendation: C.** It is A's zero-cost carry with one rule added: never remove what you have not verified.

**Optional extra for repairability:** an origin may also wait to cut its own history until the lowest `sealed_through` its consumers report. That guarantees every consumer verified it while the origin could still resend it. It costs one value per manifest request and makes origin cuts depend on the slowest consumer, so it should be a setting, off by default.

### Q4 options (what stays for removed history)

| | a. Per-stream carried, forever | b. Compact to per-type totals | c. Per-stream for the resting period, then folded into the epoch |
|---|---|---|---|
| Permanent storage | one bucket row per cut (stream, type), forever: grows with cut streams | O(types x tenants) | **no new rows**: one `removed_*` column set on epoch rows that already exist |
| Drill-down names streams below the floor | always | never | within the resting period (default 14 days, Q6); after that, by epoch only |
| Seal re-check over pruned epochs | yes | **no**: it cannot tell survivors from removed | **yes**: recompute survivors, add `removed`, compare to the seal |
| Per-tenant erasure | delete the tenant's rows | needs per-tenant totals | per-tenant epoch rows, already keyed by tenant |
| Fits "operational data rolls" (decision 5) | no | yes | **yes**: the per-stream detail is the rolling part, the epoch column is the state part |

**Recommendation: c.** Per-stream carried detail is operational data with the standard resting period, kept so a recent cut can be investigated. After that it folds into the epoch's `removed` contribution. The permanent cost is columns on rows that already exist. Nothing grows with events or with the number of cut streams.

### Together
A cut happens only below this service's own seal (C). It moves the removed digest into the stream bucket's carried columns for 14 days (c), then folds into the epoch. That means:
- no answer, seal, epoch or generation ever changes because of a cut;
- the re-check can still verify what survives;
- repair never reaches below a floor;
- the bookkeeping rolls off on its own.

## 8. Owner answers (2026-11-15)
- **Q1.** Superseding types count the key (section 2.4, option A).
- **Q2.** Cuts carry their digest, gated on the seal (section 8a, option C). The origin-waits-for-consumers extra is a setting, off by default.
- **Q3.** The fingerprint covers the types a pair shares, only.
- **Q4.** Per-stream carried detail for the resting period, then folded into the epoch's `removed_*` columns (section 8a, option c).
- **Q5.** The recommended defaults, all configurable: halted-pair alert after 2 hours; `SealRecheck` Lookback 30 days and RecheckEvery 7 days when turned on.
- **Q6.** Operational data rests 14 days by default, configurable per category.
- **Phase 1b input (2026-11-15):** pausing the development database's logical-replication consumer for 20 minutes made no measurable difference to server CPU or IOPS, so logical replication is not a cause of the idle CPU floor; #2351 goes first in phase 1b.
