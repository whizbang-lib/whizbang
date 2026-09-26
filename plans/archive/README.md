# Archived plans

Completed or superseded planning docs, moved here on 2026-07-19 so that `plans/` reflects only
**live** work. They are kept for historical reference — the work they describe has shipped (see the
`CHANGELOG.md` and the [Release 1.0 Planning](https://github.com/orgs/whizbang-lib/projects/1) board)
or has been superseded.

- **Current roadmap:** `../v1-v2-roadmap.md`
- **What shipped, when:** the Release 1.0 Planning board's Done column (each card carries a Shipped date).

What's here:
- **Shipped framework subsystems** — DLQ + NOTIFY-first coordination, sagas, offloading, the unified
  work-coordinator, the v0.2.0 streams/policies/observability spine, deserialization-registry
  consolidation, composite & collective events, stream-affinity, rewind-completion, schema-qualified
  functions, nested type-name registration, guarded lease renewal, strongly-typed id providers, and
  assorted fixes.
- **Completed reference-app phases** — the ECommerce dogfood `phaseN` design docs (phases 2–11).
- **Superseded** — `transport-adapters-full-capabilities.md` (Kafka/EventHub are out of the runtime
  stack; its interfaces were removed).

Docs that are still in-progress, partial, or reference material (e.g. the GA-gate checklist, XML-doc
completion, the still-open receive-parity / failure-plumbing items, and the v2 backlog docs) remain in
`plans/`.

**Archived 2026-09-26** (board reconcile; the work shipped or its remainder is a board card):
canonical temporal storage, case-insensitive indexes, DI registration integrity and findings, the
v0.2.0 docs-site update, the idle band, bounded inbox acquisition, the index-kind diagnostic, full lens
index coverage, priority on the wire and the priority/composite integrity plan, saga continuations,
the startup hardening round, the transport topology arc, database load under bulk import, the PR
readiness scripts and their API reference, the Phase 12 integration-test design, session notes, the
v0.1.0 release plan (the GA checklist, still the source for its board card), and the rank-aware claim
guard (superseded). Two shipped plans stay in `plans/` because migrations cite their paths:
`dlq-stack-intelligence.md` and `inbox-work-state-side-table.md`.

