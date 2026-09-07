using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Resilience;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Targeted coverage for <see cref="StreamRateLimiter"/>'s lazy stale-entry prune sweep — a branch
/// the broader <see cref="StreamRateLimiterTests"/> suite never reaches because no existing test
/// drives the limiter past 100 calls. Deterministic: <see cref="StreamRateLimiterOptions.StaleEntryTimeout"/>
/// is configured to <see cref="TimeSpan.Zero"/> so any measurable elapsed time — guaranteed by
/// simply making more calls — counts as stale, never a sleep.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Resilience/StreamRateLimiter.cs</code-under-test>
public class StreamRateLimiterCoverageTests {

  [Test]
  public async Task TryAcquire_LazyPruneSweepEvictsAStaleEntry_StreamRestartsWithAFreshWindowAsync() {
    // If a long-lived stream's tracking entry is never evicted, StreamRateLimiter's internal
    // dictionary grows without bound for the lifetime of the process — the exact per-stream memory
    // leak _pruneStaleEntries exists to prevent. Zero StaleEntryTimeout makes every entry stale the
    // instant any further calls happen, so pruning fires deterministically on the 100th call without
    // waiting on the wall clock.
    var options = new StreamRateLimiterOptions {
      MaxEventsPerWindow = 1,
      WindowDuration = TimeSpan.FromHours(1),
      CooldownDuration = TimeSpan.FromSeconds(30),
      StaleEntryTimeout = TimeSpan.Zero,
    };
    var limiter = new StreamRateLimiter(options);
    var staleStream = Guid.NewGuid();

    // Call #1: creates staleStream's entry (Count=1; allowed, since 1 > MaxEventsPerWindow(1) is false).
    await Assert.That(limiter.TryAcquire(staleStream)).IsTrue();

    // Calls #2-#100: unrelated filler streams. The 100th call triggers the lazy prune sweep
    // (every 100 calls), which — since StaleEntryTimeout is zero — evicts every entry whose window
    // has aged at all, including staleStream's, well before this loop naturally re-touches it.
    for (var i = 0; i < 99; i++) {
      limiter.TryAcquire(Guid.NewGuid());
    }

    // Call #101: if staleStream's entry survived pruning, this would be its SECOND hit within the
    // (1-hour) window — Count becomes 2, exceeding MaxEventsPerWindow(1), and with a positive
    // CooldownDuration TryAcquire returns false. A true return proves the stale entry was actually
    // evicted and the stream restarted with a fresh window, not merely that pruning ran.
    await Assert.That(limiter.TryAcquire(staleStream)).IsTrue()
      .Because("the stale entry must be evicted by the prune sweep, not merely aged in place, so the stream gets a fresh window");
  }
}
