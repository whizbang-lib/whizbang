# Bounded batch strategies and the startup settings report (#2220, #2251)

Status: **in progress**, started 2026-11-10. Ships in one PR with #2246 (set-based perspective writes); this branch is merged
into `feat/2246-set-based-perspective-writes` before the PR opens.

Decisions are recorded on the issues:
- #2220: https://github.com/whizbang-lib/whizbang/issues/2220#issuecomment-6785784674
- #2251: the issue body

## Order

1. **Done.** #2251: `SensitiveString` (redacts in ToString, logging and serialization; binds from configuration under AOT; one
   explicit call reveals it), the credential-named-plain-string diagnostic, and a value type for derived settings that
   records its inputs.
2. **Done.** #2251: the startup settings report, driven by the configuration key manifest, with the on/off setting, one structured
   entry per section, redaction by type, and a completeness test against the manifest.
3. **Done.** #2220 Part 1: `BoundedOutboxBatchStrategy` and the inbox equivalent: message and byte limits (set number or a
   multiplier of `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`), the soft limit with a 2x hard ceiling for callers in
   an open transaction, the interactive reserve reusing the gate's fraction, room released on flush completion,
   signal-based waits, wrapping applied at registration with a test, metrics, and limits reported through step 2.
4. #2220 Part 2: cross-stream coalescing outbox batch strategy.
5. Move existing derived values onto the derived-setting type: gate size from pool size, thread-pool floor, perspective
   and dispatch widths, the inbox dispatch clamp.

## Open after steps 1 to 3

- A strategy registered directly as `IOutboxBatchStrategy`/`IInboxBatchStrategy` (not through `AddWhizbang*Strategy`)
  resolves unwrapped; the source guard covers the library's own registrations only.
- The report shows a default as `(default)` without its value; showing the value needs the options instances.
- The `data/work-coordinator-strategies` docs page still recommends `Batch` for bulk imports (part of #2220 Part 2).
