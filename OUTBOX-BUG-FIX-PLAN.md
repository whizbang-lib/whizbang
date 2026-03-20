# Diagnostic Report: Whizbang Outbox Publishing Bug (0.9.7-alpha.20)

## Context

After upgrading Whizbang from `0.9.6-alpha.71` to `0.9.7-alpha.20`, outbox messages accumulate indefinitely and are never published to RabbitMQ. This blocks all cross-service event delivery. The alpha.20 fix (commit `98e25656`) added `IWorkChannelWriter` support to all strategies, but **the generated singleton registration template was not updated**, so the `IntervalWorkCoordinatorStrategy` singleton is constructed without a channel writer and silently drops all outbox work.

---

## Root Cause

### The Bug: Generated Code Missing `workChannelWriter` Parameter

The `IntervalWorkCoordinatorStrategy` and `BatchWorkCoordinatorStrategy` are registered as **singletons** via generated code from `EFCoreSnippets.cs`. The alpha.20 fix added `workChannelWriter` to the factory method (`WorkCoordinatorStrategyFactory.Create`) and the strategy constructors, but **did not update the generated singleton registration template**.

**Generated singleton registration (EFCoreSnippets.cs:179-193) — BROKEN:**

```csharp
services.AddSingleton<IntervalWorkCoordinatorStrategy>(sp => {
    return new IntervalWorkCoordinatorStrategy(
        coordinator: null,
        instanceProvider,
        options,
        logger,
        scopeFactory,
        lifecycleMessageDeserializer: ...,
        tracingOptions: ...
        // workChannelWriter: NOT PASSED — defaults to null!
    );
});
```

**Factory method (WorkCoordinatorStrategyFactory.cs:82-97) — CORRECT but unused for Interval/Batch:**

```csharp
return new IntervalWorkCoordinatorStrategy(
    coordinator, instanceProvider, options, logger,
    scopeFactory: ...,
    workChannelWriter: channelWriter  // CORRECTLY passed
);
```

**Result:** `WorkCoordinatorFlushHelper.ExecuteFlushAsync` receives `workChannelWriter: null`, so the channel-write block at line 151 is skipped:

```csharp
if (workChannelWriter != null && workBatch.OutboxWork.Count > 0) {
    // NEVER EXECUTES because workChannelWriter is null
}
```

Messages are stored in `wh_outbox` (status=1) but never written to the channel for the publisher worker to consume.

### Why Scoped Strategy Works

The `ScopedWorkCoordinatorStrategy` is created per-scope via `WorkCoordinatorStrategyFactory.Create()` which DOES pass `channelWriter`. Additionally, Scoped publishes inline in the same `ProcessWorkBatchAsync` call — it doesn't rely on a background timer.

---

## File to Fix

**`src/Whizbang.Data.EFCore.Postgres.Generators/Templates/Snippets/EFCoreSnippets.cs`**

Lines 179-193 (IntervalWorkCoordinatorStrategy singleton) and lines 194-208 (BatchWorkCoordinatorStrategy singleton) need to resolve and pass `IWorkChannelWriter`:

```csharp
services.AddSingleton<IntervalWorkCoordinatorStrategy>(sp => {
    var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
    var options = sp.GetRequiredService<WorkCoordinatorOptions>();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var logger = sp.GetService<ILogger<IntervalWorkCoordinatorStrategy>>();
    var channelWriter = sp.GetService<IWorkChannelWriter>();           // ADD THIS
    return new IntervalWorkCoordinatorStrategy(
        coordinator: null,
        instanceProvider,
        options,
        logger,
        scopeFactory,
        lifecycleMessageDeserializer: sp.GetService<ILifecycleMessageDeserializer>(),
        tracingOptions: sp.GetService<IOptionsMonitor<TracingOptions>>(),
        workChannelWriter: channelWriter                                // ADD THIS
    );
});
```

Same change needed for `BatchWorkCoordinatorStrategy` at lines 194-208.

**Also add `WorkCoordinatorMetrics` and `LifecycleMetrics`** if the constructor accepts them (check the current constructor signature).

---

## Live Database Evidence (2026-03-19, on alpha.20)

### Outbox State

| Service | Total | Stuck | Published | Strategy |
|---------|-------|-------|-----------|----------|
| **JobService** | 56 | **56** | 0 | `Interval` (singleton, no channelWriter) |
| **WorkflowService** | 4 | **4** | 0 | `Interval` (singleton, no channelWriter) |
| **BffService** | 140 | 0 | 140 | Scoped (per-scope, has channelWriter) |

### Stuck Message Details (JobService)

- All status=1 (Stored), failure_reason=99 (Unknown/default), attempts=0
- Lease renewals happening every ~300s (orphan reclaim cycle runs but doesn't help because orphan-reclaimed messages also go through the same null channelWriter path)
- Message types: JobTemplateSection events, SystemSeed events, PrintProfile events
- All created within seconds of each other during system seeding

---

## Affected JDNext Services

| Service | Strategy | File | Line | Affected |
|---------|----------|------|------|----------|
| ChatService | `Immediate` | `Program.cs` | 255 | Per-scope via factory (may work since factory passes channelWriter) |
| JobService | `Interval` | `Program.cs` | 293 | **YES — singleton, no channelWriter** |
| WorkflowService | `Interval` | `Program.cs` | 45 | **YES — singleton, no channelWriter** |
| PdfService | `Interval` | `Program.cs` | 47 | **YES — singleton, no channelWriter** |
| UploadService | `Interval` | `Program.cs` | 49 | **YES — singleton, no channelWriter** |
| NotificationsService | `Interval` | `Program.cs` | 37 | **YES — singleton, no channelWriter** |
| BffService | Scoped (default) | - | - | No — works correctly |

---

## Verification After Fix

1. Rebuild Whizbang packages (alpha.21+) from updated generators
2. Update JDNext to new package version
3. Wipe databases (`/wipe-db`) and restart
4. Check: `SELECT count(*) FILTER (WHERE processed_at IS NULL) FROM wh_outbox;` in JobService should be 0
5. Check: BffService inbox should show new arrivals from JobService
6. Check: Frontend should display job template data

---

## Secondary Issue: Orphan Reclaim Also Broken

Even when the orphan reclaim cycle claims messages and they go through `process_work_batch`, the returned outbox work is passed back through `WorkCoordinatorFlushHelper.ExecuteFlushAsync` with the same null `workChannelWriter`. So orphan reclaim can never fix the problem either — it's a dead end.

The `WorkCoordinatorPublisherWorker._processWorkBatchAsync` (line 548-572) passes `NewOutboxMessages = []` and does NOT go through the flush helper — it calls `ProcessWorkBatchAsync` directly. Any orphaned work returned there is NOT written to the channel because the worker only writes completions/failures/lease renewals, not new outbox work. The worker relies on the strategy (or flush helper) to write work to the channel.
