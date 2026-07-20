# Whizbang Release Roadmap — v1.0 & v2.0

> Source-of-truth companion to the GitHub project boards. The boards are the live tracker; this
> doc is the narrative: how we got here, what shipped, what remains, and where the 1.0 line falls.
>
> - **v1 board — [Release 1.0 Planning](https://github.com/orgs/whizbang-lib/projects/1)**
> - **v2 board — [Release 2.0 Planning](https://github.com/orgs/whizbang-lib/projects/2)**
>
> _Assembled 2026-07-19 from a full sweep of the `plans/` + `ai-docs/` backlog, the docs-repo
> proposals, every branch/worktree, and all 274 merged PRs. Status was verified against `src/` and
> the PR history — not the (stale) CHANGELOG._

---

## How to read the boards

Both boards share the same fields: **Status** (Todo / In progress / Done), **Priority** (P0/P1/P2), **Size** (XS–XL).

- **v1 board = the whole 1.0 scope.** Its **Done** column is the backfilled record of everything already shipped (reconstructed from 274 PRs, since the commit history was squashed). Its **Todo** column is the remaining road to 1.0.
- **v2 board = post-1.0.** Everything deferred past the first stable release.

### Scope decisions that shaped the split
1. **1.0 holds the full retention story.** The ephemeral → destruction/TTL → temporal-engine → archival → carry-forward → GDPR-crypto-shred program is **in 1.0**, not deferred. 1.0 ships when that story is complete.
2. **Sagas, Composite events, and Collective events are part of the 1.0 *stable* surface** — they carry a stability guarantee at 1.0, so finishing their docs/hardening are v1 blockers (not "experimental for 1.0").

---

## The road to 1.0 so far (historical arc)

Version numbers are **not** milestones here: GitVersion runs in continuous-deployment/patch mode, so every commit and release branch bumps the number (the minor version climbs into the hundreds purely as commit arithmetic). The project is **still pre-1.0**; 1.0 has never been cut.

- **Foundation (2026-01):** the entire core landed as one squashed "Foundation Release" — dispatcher/receptors/envelopes, event-sourcing store, CQRS, Postgres UUIDv7 + JSONB, EF Core 10 + Dapper stores, Azure Service Bus + RabbitMQ transports, source generators, CLI, and the `whizbang migrate` tooling. All granular pre-foundation history lives inside that one commit.
- **The roadmap then fragmented.** Three original tracks — a GA/release-engineering checklist, a dogfood reference-app plan, and the framework-spine plan (streams / policies / observability) — gave way to ~dozens of per-epic plan files plus the CHANGELOG as an (unmaintained) ledger. Consolidating that sprawl is what these boards are for.
- **~6 months, 274 merged PRs, 20+ epics** built the spine: scope/security, policy engine, work-pump & perspective lifecycle, snapshots/rewind, stable type identity, throughput, DLQ + NOTIFY-first coordination, offloading, event upcasting, sagas, composite & collective events, cascade context, transport ordering.

---

## What has shipped (the 1.0 Done column)

Reconstructed from the 274 merged PRs and verified in `src/`. Each is a Done card on the v1 board.

| Area | Shipped |
|---|---|
| **Foundation** | Core framework (dispatcher, event store, EF Core/Dapper, ASB, generators, CLI); `whizbang migrate` + unified REST/GraphQL endpoint generation |
| **Scope & security** | `[InheritScope]`, `IStreamScopeEvent`, `RequirePermission` + claim aggregation, scope-column population |
| **Observability** | OpenTelemetry metrics across 6 meters |
| **Work coordination & perspectives** | LifecycleCoordinator + PostLifecycle/PostAllPerspectives/ImmediateAsync/FireAt; work-pump decomposition + per-stream drain + single-writer ownership; production hardening (gate deadlines, timeouts, empty-StreamId sentinel) |
| **Rewind** | Perspective rewind: detection, observability, startup scan, rebuilder |
| **Type identity** | `[PinnedId]` + registry + analyzer/code-fix; pinned-type ledger (governed renames + reconcile) + fingerprint migrations |
| **Throughput** | Drain mode (3.4×); bulk-import throughput + connection-pool-exhaustion fix |
| **DLQ / coordination** | NOTIFY-first / zero-idle-polling + pinned worker pool; full DLQ pipeline + forensic preservation; turnkey LISTEN/NOTIFY data-source (SCRAM-SHA-256) |
| **Offloading** | Claim-check large-message offload (`Whizbang.Offloads.AzureBlob`) |
| **Serialization** | Event upcasting + serialization / size-aware versioning; jsonb polymorphic `$type` round-trip fix |
| **Sagas** _(stable in 1.0)_ | `PublishOnceAsync` + `Whizbang.Sagas` + framework-managed completion |
| **Composite events** _(stable in 1.0)_ | Turnkey durable dispatch-time fan-out |
| **Collective events** _(stable in 1.0)_ | Cross-perspective cohorts + pluggable apply hooks |
| **Cascade context** | W3C correlation, `AutoPopulate`, cascade-identity propagation |
| **Transports** | FIFO ordering + batch receive + resilient transport |
| **Eng** | CI/CD trusted-publishing pipeline; coverage-to-100% + mutation testing |

---

## Remaining road to 1.0 (the v1 board — reconciled)

> **Reconciled 2026-07-19:** every card that looked "remaining" was re-verified against the current
> code + PRs. Most were already fixed and merged; the ephemeral program is **built-but-unmerged**, not
> unstarted. What's genuinely left is small. Board now reads **36 Done / 9 In progress / 8 Todo**.

### 🔄 In progress — implemented on `feature/ephemeral-core`, unmerged (the merge train)
The whole retention program is built *with tests* on the 117-commit `feature/ephemeral-core` stack, in
dependency order **F1 → F2 → E1 / fingerprint / snapshots → E2 → A1 → E3**. None is on `develop` yet
(no open PRs), so it ships as one train — F1 first.
- **F1 signal bus** — transport-agnostic `ISignalBus` + in-memory/Postgres transports + durable `wh_signals` log. Most merge-ready; the stack sits on top of it.
- **E1 ephemeral events · E2 destruction/TTL · F2 temporal engine · reap-driven snapshots · A1 archival/compaction · E3 carry-forward · fingerprint lineage/reclassification.**
- **XML-doc completion** — `Whizbang.Core` + `Generators` now enforce CS1591 (the bulk, regression-locked via build failure); other packages still suppressed.

### ⛔ Genuinely open (the true remaining v1 work)
- **Collective/perspective failure plumbing** — a failed apply still can't report Failed/backoff (`EventWorkId`/`FailureReason` triple-mismatch → 0-row UPDATE).
- **Transport receive parity** — RabbitMQ silently drops on registry-miss; no `IMessageReceiveResolver` yet.
- **GDPR crypto-shredding (G1)** — the one true greenfield item; only `Disposition.CryptoShred`/`Erasure` enum placeholders exist.
- **`OnPerspectiveCompleted` hook** — a first-class perspective-completion API gap the sample exposes.
- **Reconcile the GA-gate checklist** (still 0/11 though the infra exists) and **reconstruct the CHANGELOG** (omits ~13 shipped epics).
- **Restore lease-renewal regression tests** — the bug is fixed, but its RED tests were dropped in a refactor; re-lock the invariant.

### ✅ Verified already shipped (reconciliation moved these off the backlog to Done, each with a Shipped date)
Perspective stream affinity · rewind completion gap · saga-completion race (closed by the affinity gate;
the rank-aware perf *guard* re-land is a v2 item) · schema-qualified `process_work_batch` · nested
type-name registration · guarded lease renewal · two-flow DLQ recovery · strongly-typed id providers ·
composite-events docs · collective-events docs + open-set serialization · sagas cross-pod completion.
All merged to `develop`.

### Non-blocking
- **Reference-app (ECommerce)** — its Phase-12 E2E tests are green in CI; it's a dogfood sample, not the shipped library. Stays on the board marked non-blocking (Phase 13 docs + one parked InMemory test remain).

---

## Post-1.0 (the v2 board — reconciled)

> **Reconciled 2026-07-19:** 4 cards were already shipped (moved to Done), 3 were part-shipped (rescoped to their remaining half). Board now: **4 Done / 10 Todo.**

- **Open — performance:** throughput slices 2/5/6 (slice 4 is telemetry-blocked by design); rank-aware claim-work guard (re-attempt after the saga fix); perspective priority tiers; collective↔standard shared advisory lock (§5b, intentional deferral).
- **Open — infra / tooling:** `RoundRobinPartitionRouter`; `Whizbang.Debugging` LSP keepalive host (only a pause-state scaffold exists); message-registry typed-model refactor; worker-level receptor chaos scenarios (primitives exist, worker wiring doesn't).
- **Rescoped (part shipped):** VSCode extension → dev-time nav shipped (v0.8.0); runtime-debugging suite remains. Docs-site → v0.2.0 gaps largely filled; the `spec/` "porter" behavioral-spec tree (the JS/TS-port foundation) is still a stub.
- **✅ Already shipped → Done:** Service Bus auto-provisioning (full DX) · offload blob cleanup (delete-on-consume; time-TTL delegated to Azure lifecycle) · SyncMode/`[Obsolete]` API hygiene · flaky WorkCoordinator-options test.

---

## Housekeeping (tracked, low-noise)

- **Archive the DONE plan docs** in `plans/` (DLQ, sagas, offloads, work-coordinator-unified, v0.2.0 spine, and the completed `phaseN` reference-app docs) so `plans/` reflects only live work.
- **`transport-adapters-full-capabilities.md` is superseded** — Kafka/EventHub are out of the runtime stack and its interfaces were removed.
- **Docs repo:** the *older* `proposals/` suite (event-store, multi-tenancy, policy-engine, concurrency, …) mostly shipped already in evolved form → reclassify/archive; the newer `:::planned` retention/GDPR proposals are the real forward set.

---

## How this was assembled (method)

Four parallel read-only passes: (1) classified every `plans/` + `ai-docs/` doc by status against `src/`; (2) mined the docs repo for proposals incl. its other branches; (3) forensically checked all 481 refs + 11 worktrees for stranded proposals (none in the code repo — the retention/GDPR proposals live only on docs-repo branches); (4) reconstructed the shipped history from all 274 merged PRs (Jan–Jul 2026), since the commit history was squashed. Every PR was bucketed exactly once; reverts and still-pending work were flagged so the Done column reflects only truly-shipped work.
