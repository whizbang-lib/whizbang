# Whizbang Release Roadmap — v1.0 & v2.0

> Source-of-truth companion to the GitHub project boards. The boards are the live tracker; this
> doc is the narrative: how we got here, what shipped, what remains, and where the 1.0 line falls.
>
> - **v1 board — [Release 1.0 Planning](https://github.com/orgs/whizbang-lib/projects/1)**
> - **v2 board — [Release 2.0 Planning](https://github.com/orgs/whizbang-lib/projects/2)**
>
> _Refreshed 2026-09-26 (board reconcile). Assembled 2026-07-19 from a full sweep of the `plans/` + `ai-docs/` backlog, the docs-repo
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

## Remaining road to 1.0 (the v1 board)

> **Reconciled 2026-09-26** against develop, every merged PR since 2026-07-19 and every issue. The
> retention train (F1, F2, E2, A1, snapshots, fingerprint lineage) merged on 2026-07-21 (#355); 21
> capabilities that shipped as PRs without an issue now have Done cards with Shipped dates (stream
> integrity, transport topology, message priority, perspective indexing, the release pipeline, and more).
> Board: **195 Done / 2 In progress / 13 Todo.** The board is kept current as work happens (the
> `whizbang-board` skill); this section is its narrative.

### In progress
- **E1 ephemeral events**: `TransientStorage.InMemory` is documented but not yet enforced at runtime.
- **E3 carry-forward tier 2**: `StreamCompactor` shipped; the reaper trigger (`Disposition.Compact`) and the
  compacted-record upgrade runner remain.

### Open
- **P0: Transport receive parity.** RabbitMQ drops on a registry miss; no `IMessageReceiveResolver` yet.
- **P1: Canonical temporal rewrite on a store's first 1.0 boot.** One unbounded transaction across every
  table: a restart loses all progress, and a table past its 600-second timeout never converts. Needs
  per-table, batched, resumable conversion with visible progress.
- **P1: A failed collective apply records nothing against its rows.** The standard and drain paths were fixed
  (#770, #771); the collective sink still reports an event id where the failure function matches work ids.
- **P1: First-class `OnPerspectiveCompleted` hook.**
- **P1: Idle-band settings have no effect.** `IdleBandOptions` is never registered, so its limits and
  per-type opt-ins change nothing.
- **P1: Reconcile the GA gate checklist** (`plans/archive/v0.1.0-release-plan.md`, 0 of 434 boxes) into the
  real 1.0 exit criteria.
- **P1: An abandoned release cut can leave develop publishing below NuGet** (#872).
- **P2: GDPR crypto-shredding (G1)**, the one greenfield item.
- **P2: XML docs for every package**, with `<docs>` and `<tests>` links so code, the docs site and the
  tests point at each other (Core and Generators already enforce CS1591).
- **P2:** stream-lease write-volume regression tests, the heartbeat watchdog's alive-lock source, and the
  Dapper nested-class schema-hash collision.

### Non-blocking
- **Reference app (ECommerce)**: a dogfood sample, not the shipped library (Phase 13 docs remain).

---

## Post-1.0 (the v2 board — reconciled)

> **Reconciled 2026-07-19:** 4 cards were already shipped (moved to Done), 3 were part-shipped (rescoped to their remaining half). Board now: **4 Done / 10 Todo.**
>
> **Reconciled 2026-09-26:** 6 cards added (MC/DC coverage, read/write connection separation, system-table
> index candidates, a generated DI manifest, priority steps 5-7, database-load follow-ups); the rank-aware claim
> guard archived as superseded; the throughput card narrowed to slices 2 and 5. Board now: **4 Done / 15 Todo.**

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
