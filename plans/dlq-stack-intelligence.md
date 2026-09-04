# DLQ Stack Intelligence & Cohort Canary Recovery

Status: DESIGN AGREED (2026-09-03) — Phase 0 not started
Owners: framework
Prereqs: shipped — idle-arbitrated recovery + deferral floor (#640/#649/#652), fingerprint v1 (053), generation replay, turnkey options binding.

## Problem

A mass dead-letter event ends today in HeldForReview: rows that spent their per-reason
recovery budget park permanently, even when the failure was a bug the very next deploy
fixes. The attempt count is evidence about a BUILD, not about the message — holding
forever treats stale evidence as permanent truth. Meanwhile operators have no way to see
which failure SHAPES are growing, shrinking, or newly born.

## Design

### 1. Stack identity — relational model (the backbone)

    wh_stack_frames (frame_id, frame, normalization_version, first_seen)
    wh_stacks       (stack_id, sequence_hash, frame_count, first_seen)
    wh_stack_links  (stack_id, position, frame_id)  PK(stack_id, position)
    wh_dead_letters + stack_id (nullable)

- sequence_hash = SHA over the FULL ordered, normalized in-app frame list (no 3-frame cap).
  Derivable from error_text alone — pure CPU.
- Exact identity (cohort key) = stack_id. Similarity = queries over links (GIN on frame
  arrays / join-based overlap ranking), NOT digests. XOR/SimHash set-digests: DEFERRED —
  only if scale outgrows join-based overlap. If ever added, key per-frame hashes by
  (frame, k-th occurrence) so recursion cannot cancel and insertions do not cascade.
- Frames table is the single scrubbing point, versioned like fingerprint_version.

### 2. Normalization v2 (fingerprint version bump + frame rules)

- Async state machines: `<Method>d__N.MoveNext` → `Method`.
- Innermost exception type wins over outer wrapper.
- Prose errors (no exception type): scrub digits/hex/GUIDs/quoted strings from line 1,
  hash the template.
- Exclusion list broadens to ALL framework plumbing namespaces; consumer frames fill the
  slots, deepest framework frame only as fallback. (Discrimination lives in exclusions,
  not frame count — 3-frame exact fingerprint stays as legacy/fallback key only.)

### 3. Write path — two-layer split

- INLINE at dead-letter time: parse + sequence_hash + metric emit + stack_id in the WRN
  log. Microseconds; no DB normalization on the hot path.
- ASYNC in maintenance (idle-arbitrated, version-aware — same machinery as the
  fingerprint backfill): upsert frames, stacks, links; stamp wh_dead_letters.stack_id.
  Storms dedupe: 55k rows ≈ a handful of stacks; first row per stack pays, the rest hit
  the unique index.

### 4. Cohort canary recovery

- Cohort = stack_id (fingerprint fallback for unnormalized rows).
- Config (bound turnkey under Whizbang:DeadLetterRecovery):
    RetryHeldOnStartup: Off | Canary | Full     (default Off; operator sets + restarts)
    CanaryProbeSize: 10
    GenerationBudget: 3
- New build generation ⇒ held cohorts become probe-eligible (generation replay extends to
  Held): re-drive CanaryProbeSize rows per cohort; probes succeed ⇒ auto-release cohort;
  fail ⇒ cohort stays held, one generation credit consumed. After GenerationBudget
  distinct generations fail: permanent-pending-operator.
- Exponential backoff within a generation: cooldown = policy base × 2^attempt, capped.
- Operator disposition trumps everything (HoldIndefinitely never probes).
- Everything flows through housekeeping arbitration (idle-gated + deferral floor).
- Similarity-driven widening ("these cohorts share 5 of 6 frames — include?") is operator
  tooling, never automatic: over-grouping is the dangerous direction for auto-release.


### 4a. Canary retry mechanics

- SELECTION: stratified across the cohort (distinct message_type x age bands), not
  random — a cohort can span types that merely share a code path.
- EXECUTION: a probe IS a normal re-drive through the existing redelivery machinery
  (same pacing, same arbitration); its dead_letter_id is recorded as in-flight probe
  state. Outcome correlation rides message_id (redelivery preserves it — see 125):
  inbox completion of that message_id = success; a NEW dead-letter row with that
  message_id = failure, linked back as evidence.
- VERDICT: async, at the next scan after ProbeSettleWindow (default one scan interval).
  All completed = Pass. All failed = Fail (generation credit spent, cohort re-held).
  Neither by the window = inconclusive: one probe retry, then fail conservative.
  Verdicts never block scan cycles.
- RELEASE = ELIGIBILITY, NOT A FIREHOSE: pass flips the cohort Held -> Pending with
  STAGGERED next_recovery_at; the existing paced machinery drains it (ScanBatchSize per
  granted scan, idle-arbitrated, deferral-floored). Full mode skips the verdict but
  keeps the staggered release — a trust shortcut, never a pacing shortcut.
- REASON-18: observation counts scope to the build generation, like attempt budgets —
  otherwise a bound-hit row auto-fails every probe and those cohorts are unprobeable.

### 4b. Partial success — Mixed verdicts and trickle release

Verdicts are NOT all-or-nothing (empirical: the 2026-09-03 ack cohort spans 34
message_types under one fingerprint — types can and will diverge on probe):

- Verdicts: Pass | Fail | MIXED.
- Mixed -> TRICKLE RELEASE: rate-limited waves (wave 1 = CanaryProbeSize rows, doubling
  per clean wave — AIMD-shaped), halting and re-holding the remainder when a wave
  re-dead-letters above tolerance. Progressive rollout, storm-impossible.
- Mixed verdicts REPORT THE SPLIT by stratum: failures concentrated in specific
  message_types produce a proposed cohort split by type — over-grouping diagnosis
  automated, split approval human.
- Row-level outcomes track independently of cohort verdicts: a probe-failed row spends
  ITS budget; clean-wave rows mark recovered normally.

### 4c. Cohort state persistence

    wh_dlq_cohorts (cohort_key, generation, state, probes_sent, probes_succeeded,
                    probes_failed, waves_released, rows_released,
                    generation_credits_left, probe_started_at, verdict_at, verdict)
    state: Held | Probing | Trickle | Released | PermanentPendingOperator

One row per (cohort, generation) — the full campaign history, joinable to
wh_dead_letters via cohort_key.

### 4d. OTel for cohort state

- whizbang.dlq.cohorts{state} (UpDownCounter): live census — the one-panel answer to
  "where is the recovery program right now".
- whizbang.dlq.cohort_verdicts{stack_id, verdict=pass|fail|mixed} (Counter).
- whizbang.dlq.release_waves{stack_id, outcome=clean|halted} (Counter).

### 5. Telemetry — the two-layer contract

- REAL-TIME (Meter API, inline hash): whizbang.deadletter.arrivals{stack_id,
  failure_reason}, canary probes/releases{stack_id}, crossed with build generation.
  New-stack-after-deploy = the early-warning alarm. Cardinality: per-process cap on
  distinct stack_id tags, overflow bucketed as "other". Registered in WhizbangMeters.All.
- RETROACTIVE (tables): dead_lettered_at × stack_id answers "which stacks failed over
  time" with true original timestamps via SQL/KQL — never backdated metrics (the Meter
  API cannot stamp, backends punish out-of-order samples, and the idle-gated backfill
  would delay the signal most during storms, exactly when it matters).

## Phases

- P0 — corpus validation: FIRST PASS DONE (2026-09-03, read-only, live slot): 81k held
  rows, 100% carry error_text, ZERO carry stack frames — the dominant failure classes
  (lease expiry, observation bound, broker quarantine) are prose, and v1 fingerprints
  already cohort them tightly (3-4 per service; ack = 52,687 lease casualties across 34
  message_types + 2,594 perspective-poison + 61 broker). CONSEQUENCES: (a) canary
  recovery works on the CURRENT corpus with v1 keys — P1 needs no schema; (b) v2
  reprioritizes prose-template normalization ABOVE frame rules; (c) the frames/stacks
  relational layer validates on the next genuine exception storm — capture is
  forward-looking by design. Remaining P0: prose-template variant comparison in scratch.
- P1 — RetryHeldOnStartup (Canary|Full) keyed on v1 fingerprints. Immediate operator
  value for the current held population.
- P2 — normalization v2 + stack tables + inline hash + backfill + OTel layer.
- P3 — generation-scoped budgets, deploy-triggered auto-canary, exponential backoff,
  operator cohort tooling (summary rollups, release/hold-cohort functions).

## Open questions

- GenerationBudget default (3 proposed).
- Whether P1 cohorts should grandfather the 2026-09-03 held population as first canaries.
  (reason-18 probing: RESOLVED — observation windows scope to the generation; see 4a.)
