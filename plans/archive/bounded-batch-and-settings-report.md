# Bounded batch strategies and the startup settings report (#2220, #2251)

Status: **shipped** 2026-11-13 in PR #2281 (started 2026-11-10). Shipped in one PR with #2246 (set-based perspective writes); this branch is merged
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
4. **Done.** #2220 Part 2: cross-stream coalescing outbox batch strategy.
5. **Done.** Move existing derived values onto the derived-setting type: gate size from pool size, thread-pool floor, perspective
   and dispatch widths, the inbox dispatch clamp.

## Decisions after steps 1 to 3 (2026-11-11)

- A strategy registered directly is wrapped where the framework resolves it (`BatchStrategyResolution`), once per
  instance; a source guard fails on any other resolution. Done.
- Room held by a custom strategy that never flushes through the registered callbacks stays documented; no cheap check
  exists without a timer per held message, so none was added.
- One shared budget per process, 10,000 messages by default: confirmed.
- The `data/work-coordinator-strategies` Batch advice is corrected with Part 2. Done.
- The startup settings report shows an unset setting's default value, marked `(default)` (owner decision, reversing the
  earlier follow-up): recorded by the key manifest test into `configuration-defaults.txt`. Done.
