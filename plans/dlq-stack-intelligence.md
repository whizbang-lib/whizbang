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

- P0 — corpus validation (read-only, scratch tables): run v1 + v2 candidates + full frame
  extraction over the ~75k held rows. Measure: dedup ratio, cohort size distribution,
  exclusion-list discrimination, overlap clustering vs known storm cohorts. Tunes v2
  before any migration.
- P1 — RetryHeldOnStartup (Canary|Full) keyed on v1 fingerprints. Immediate operator
  value for the current held population.
- P2 — normalization v2 + stack tables + inline hash + backfill + OTel layer.
- P3 — generation-scoped budgets, deploy-triggered auto-canary, exponential backoff,
  operator cohort tooling (summary rollups, release/hold-cohort functions).

## Open questions

- GenerationBudget default (3 proposed) and whether reason-18 (observation bound)
  probes at reduced size or not at all.
- Whether P1 cohorts should grandfather the 2026-09-03 held population as first canaries.
