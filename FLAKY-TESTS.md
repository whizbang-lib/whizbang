# Flaky Tests

Tests listed here have been observed to fail intermittently and need redesign for consistent execution.

## Known Flaky Tests

### `DebuggerAwareClock_WithCpuTimeSampling_HandlesMultipleSamplesAsync`

- **File**: `tests/Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs`
- **Issue**: Timing-sensitive test that relies on CPU time sampling. Results vary depending on machine load and scheduling.
- **Root Cause**: The test measures elapsed CPU time across multiple samples, which is inherently non-deterministic.
- **Fix Strategy**: Replace time-based assertions with behavior-based assertions (e.g., verify sampling was invoked the correct number of times rather than asserting specific timing values).

