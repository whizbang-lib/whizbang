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
