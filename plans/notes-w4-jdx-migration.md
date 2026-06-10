# W4 → a consumer migration: `LocalInvokeAndSyncAsync`

This doc tells the a consumer agent how to migrate a consumer callsites to the new Whizbang W4 API. Pairs with `whizbang-dispatcher-projection-sync.phase0-findings.md` (which proves the framework already does the right thing — the 380 cold-Docker integration failures are caused by a TimeSpan timeout, not a missing wait).

## TL;DR

Two changes per affected a consumer callsite:

1. **Add `SyncMode.AllProjections`** as the second argument. Read-after-write is the default expectation; make it explicit.
2. **Pass the existing `CancellationToken`** as the last argument. No more `TimeSpan` timeout; the caller's CT bounds the wait.
3. (Bonus, for the seed callsites) **Delete the `SeedHelpers.PollUntilAsync` wrapper** entirely. The dispatcher already waits for the projection.

After the migration, a consumer's 380 cold-Docker integration failures should drop to zero (or to baseline of unrelated flakes).

## What changed in Whizbang

W4 added a new overload of `LocalInvokeAndSyncAsync` that:

- Takes a `SyncMode` enum (required, no default)
- Takes a `CancellationToken` (no `TimeSpan` timeout)
- Returns `ValueTask` (not `Task<SyncResult>`)

The legacy overloads (the ones with `TimeSpan? timeout = null`) are now `[Obsolete]` — they still work, but emit a compiler warning pointing at the new API.

```csharp
// New W4 surface on IDispatcher
public enum SyncMode {
  StreamOnly,         // fast: durability only, NO perspective wait
  AllProjections,     // CQRS default: read-after-write guaranteed
}

ValueTask LocalInvokeAndSyncAsync<TMessage>(
    TMessage message,
    SyncMode mode,            // ← REQUIRED, no implicit default
    CancellationToken ct = default)
    where TMessage : notnull;
```

The new method only handles void receptors (no `TResult` overload). For typed-result patterns or per-perspective waits, keep the legacy overloads or refactor (see below).

## How to migrate each callsite

### Pattern 1 (most common): seed callsites with polling defense

a consumer has 10 of these under `src/services/a consumer.JobService/Features/SystemManagedListsFeature/Initialize/`. Example pattern:

```csharp
// BEFORE
await dispatcher.LocalInvokeAndSyncAsync(new InitializeSystemManagedListCommand { ... });
await SeedHelpers.PollUntilAsync(
  () => repo.GetByStreamIdAsync(streamId, ct),
  m => m is not null,
  TimeSpan.FromSeconds(15),
  ct);

// AFTER
await dispatcher.LocalInvokeAndSyncAsync(
    new InitializeSystemManagedListCommand { ... },
    SyncMode.AllProjections,
    ct);
// PollUntilAsync deleted — the dispatcher already waited for the projection
```

The polling was defensive code for a perceived bug that doesn't exist (Phase 0 confirmed the framework actually waits). After the migration, the read on the next line MUST see the projection.

### Pattern 2: typed-result `LocalInvokeAndSyncAsync<TMessage, TResult>`

The new W4 method is void-only. Callers needing a typed result should split into `LocalInvokeAsync<TResult>` + an optional sync step:

```csharp
// BEFORE
var result = await dispatcher.LocalInvokeAndSyncAsync<CreateOrder, OrderResult>(
    new CreateOrder { ... },
    timeout: TimeSpan.FromSeconds(10));

// AFTER (when read-after-write semantics needed)
var result = await dispatcher.LocalInvokeAsync<OrderResult>(new CreateOrder { ... });
await dispatcher.LocalInvokeAndSyncAsync(new NoOpSyncSentinel(), SyncMode.AllProjections, ct);
// — OR, keep the legacy overload until the next major; it still works, just warns.
```

If splitting isn't practical, leave the legacy overload in place. It's `[Obsolete]` but functional. The next major release will remove it.

### Pattern 3: per-perspective `LocalInvokeAndSyncAsync<TMessage, TResult, TPerspective>`

Rare pattern. The W4 method waits on ALL perspectives, not a specific one. Two options:
- Keep the legacy `<TMessage, TResult, TPerspective>` overload (still `[Obsolete]` but functional).
- Refactor to `LocalInvokeAsync<TResult>` + an explicit perspective-specific read (use the perspective's repository directly).

## Why no `TimeSpan` parameter

The proposal correctly identified that timeout-shaped APIs encourage timing-based defenses. The W4 design:

- **`CancellationToken` only** — caller bounds the wait. Wrap your own `CancellationTokenSource(TimeSpan.FromMinutes(2))` if you want a timeout; the framework doesn't provide one for you.
- **Perspective health is an observability concern**, not a per-call defense. A hung perspective should page someone (via `whizbang.lifecycle.projection_sync_wait_ms` p99 alerts), not silently throw `TimeoutException` at every callsite.

If you're tempted to wrap a `TimeSpan.FromSeconds(N)` around every call, that's a signal that the perspective isn't healthy. Investigate the perspective, don't paper over with timeouts.

## Why `SyncMode` has no default

A defaulted `SyncMode` would conflict with the legacy overload (both methods would match `LocalInvokeAndSyncAsync(cmd)`). Beyond that — explicit intent at the callsite is the cleaner design. Every callsite makes its read-after-write expectation visible to code review.

## Quick checklist for the a consumer agent

1. [ ] Grep for `LocalInvokeAndSyncAsync` in `src/services/`. Most callsites are in `SystemManagedListsFeature/Initialize/` (the 10 seed files).
2. [ ] For each: add `SyncMode.AllProjections, ct` to the call.
3. [ ] In the 10 seed files: delete the subsequent `PollUntilAsync` wrapper.
4. [ ] `using Whizbang.Core.Perspectives.Sync;` for `SyncMode`.
5. [ ] Delete `SeedHelpers.PollUntilAsync` if it has no remaining callers.
6. [ ] Re-run a consumer integration suite (especially under cold Docker). Expect 380→0 (modulo unrelated flakes).
7. [ ] If any tests STILL fail with cold-Docker timeouts, the issue is genuinely in the perspective worker — file an issue; don't add another `PollUntilAsync`.

## References

- W4 plan: `/Users/philcarbone/.claude/plans/proud-wibbling-orbit.md` (Workstream 4 section)
- Phase 0 empirical findings: `whizbang-dispatcher-projection-sync.phase0-findings.md` (in this proposals folder)
- New API: `Whizbang.Core.IDispatcher.LocalInvokeAndSyncAsync<TMessage>(TMessage, SyncMode, CancellationToken)`
- New enum: `Whizbang.Core.Perspectives.Sync.SyncMode`
- Whizbang PR: TBD (currently bundled with track-3 PR #257)
