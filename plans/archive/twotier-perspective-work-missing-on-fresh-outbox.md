# Resolved: `ProcessWorkBatch_TwoTier_*` tests were asserting on stale result shape

**Status**: resolved 2026-04-19 on `feature/receptor-firing-debug-logging`
**Original symptom**: six consecutive `TwoTier_*` tests in `EFCoreRewindDetectionTests` failed deterministically with `result.PerspectiveWork` empty when it should contain stream ids.

---

## What the failure actually was

Not a bug in the SQL pipeline. The drain-mode refactor moved the canonical result from `WorkBatch.PerspectiveWork` (per-event rows) to `WorkBatch.PerspectiveStreamIds` (distinct stream ids). `EFCoreWorkCoordinator` intentionally leaves `PerspectiveWork` empty when `perspective_stream` rows are present.

The six failing tests still asserted against the legacy `PerspectiveWork.Select(w => w.StreamId)` shape. Once they read from `PerspectiveStreamIds` instead, they all pass.

### Evidence

- `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:360-376` — when `perspective_stream` rows arrive, `perspectiveWork = new List<PerspectiveWork>()` and stream ids flow through `perspectiveStreamIds` (returned via `WorkBatch.PerspectiveStreamIds` ~line 410).
- `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql:1203-1288` — Phase 7 emits `'perspective_stream'` rows with `work_id=NULL`, `perspective_name=NULL`, Tier 1 preceding Tier 2.
- `src/Whizbang.Core/Messaging/IWorkCoordinator.cs:568-575` — both `PerspectiveWork` (legacy per-event) and `PerspectiveStreamIds` (drain mode canonical) exist on `WorkBatch`.
- `src/Whizbang.Core/Workers/PerspectiveWorker.cs:558-567` — the real worker already consumes `PerspectiveStreamIds`.
- Sibling `ProcessWorkBatch_WithOutOfOrderEvent_SetsRewindRequiredOnCursorAsync` passed because it inspects the cursor table directly and never touches the drain-mode result.

## Fix

Test-only edit to `tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreRewindDetectionTests.cs`:
- `result.PerspectiveWork.Select(w => w.StreamId).Distinct().ToList()` → `result.PerspectiveStreamIds`
- `result.PerspectiveWork.Count` → `streamIds.Count`
- Tier-ordering reads positional index from `streamIds.IndexOf(...)`

## Verification

```
cd tests/Whizbang.Data.EFCore.Postgres.Tests
dotnet run --no-build -- --treenode-filter '/*/*/EFCoreRewindDetectionTests/ProcessWorkBatch_TwoTier*'
# 6 / 6 passed

dotnet run --no-build -- --treenode-filter '/*/*/EFCoreRewindDetectionTests/*'
# 14 / 14 passed — no regression on cursor/rewind/debounce tests
```

No SQL or C# production code was changed. Migrations 022 / 029, `EFCoreWorkCoordinator`, and `IWorkCoordinator` are untouched.
