# Plan: Perspective rewind misses events appended during rewind window

> **Cross-repo pointer.** The full plan, symptom data, and slice breakdown
> live in JDNext at:
>
> `/Users/philcarbone/src/JDNext/plans/bulk-import-saga-completion-race.md`
>
> This file is a stub kept in the Whizbang repo so the work is discoverable
> from `whizbang/plans/` when someone scans the Whizbang side. The primary
> code changes are Whizbang SQL (perspective rewind completion re-check +
> cursor-advance-only-after-apply invariant). The defensive backstop is in
> JDX.

## TL;DR

`PerspectiveRewindCompleted` is published when the rewind worker thinks the
projection has caught up — but the catch-up is computed against the
event-store HEAD as of when the rewind STARTED, not as of when it
completes. Events appended to the stream during the rewind window are
silently skipped.

Slot-3 forensic on 2026-06-12 confirmed this: a 350-item bulk-import saga
had 6 rewind cycles fire in a 15 s window, and 3 `SagaItemCompletedEvent`
rows (at versions 679 / 681 / 683) landed BETWEEN a rewind's start
(v677/v678) and its eventual completion (v691). Those 3 events never
reached the projection's `Apply` chain; the saga snapshot shows
`ProcessedLineNumbers` missing the 3 corresponding line numbers, and
`CompletedItems = 347` vs `TotalItems = 350`. All 350 events ARE durable
in `wh_event_store` — the data side is correct; only the projection lags.

## Scope (Whizbang side only — see JDNext plan for the full picture)

**Slices 1–4** of the linked plan are Whizbang work:

- **Slice 1** — RED integration test
  (`tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRewindRaceTests.cs`,
  new): rewind with concurrent appends must include the appends.
- **Slice 2** — GREEN: SQL change to the rewind-completion path
  (`_emit_event_store_chain`, `complete_perspective`, or related) so that
  `PerspectiveRewindCompleted` is only published when the projection
  cursor matches event-store HEAD AT THE COMMIT MOMENT, not at the
  rewind-start moment.
- **Slice 3** — RED integration test
  (`tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveCursorAdvanceTests.cs`,
  new): `wh_perspective_cursors.last_event_id` must only advance to an
  event that has been Apply'd.
- **Slice 4** — GREEN: audit and tighten the SQL functions that advance
  the cursor.

See the linked JDNext plan for full slice details, files-to-touch, risks,
and the JDX-side defensive reconciliation Apply (slices 5/6).
