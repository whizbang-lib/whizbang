using System.Collections.Concurrent;

namespace Whizbang.Core.Sequencing;

/// <summary>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ConcurrentAccess_VariousTaskCounts_ShouldMaintainConsistencyAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:MixedOperations_GetNextAndReset_ShouldBeThreadSafeAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ConcurrentAccess_MultipleStreams_ShouldMaintainSeparateCountersAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:LargeSequenceNumbers_VariousLargeValues_ShouldHandleCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:MultipleStreams_ManyKeys_ShouldMaintainSeparatelyAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ResetDuringConcurrentAccess_ShouldNotCorruptAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:NegativeResetValue_ShouldWorkCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:SequentialAccess_VariousCallCounts_ShouldCompleteQuicklyAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ConcurrentAccess_ManyStreams_ShouldDistributeEvenlyAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:UnusedStreams_ShouldReturnMinusOneAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:GetCurrent_AfterMultipleCalls_ShouldReturnLatestAsync</tests>
/// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:CancellationToken_Cancelled_ShouldThrowAsync</tests>
/// In-memory implementation of ISequenceProvider using ConcurrentDictionary and Interlocked operations.
/// Provides thread-safe, monotonically increasing sequence numbers per stream.
/// Suitable for single-process scenarios or testing. For distributed systems, use a database or Redis provider.
/// </summary>
public class InMemorySequenceProvider : ISequenceProvider {
  /// <summary>
  /// Holds a sequence counter that can be safely incremented with Interlocked operations.
  /// Using a class (reference type) allows Interlocked.Increment to work on the Value field.
  /// </summary>
  private class SequenceCounter {
    public long Value = -1; // Start at -1 so first increment returns 0
  }

  private readonly ConcurrentDictionary<string, SequenceCounter> _sequences = new();

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ConcurrentAccess_VariousTaskCounts_ShouldMaintainConsistencyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:MixedOperations_GetNextAndReset_ShouldBeThreadSafeAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ConcurrentAccess_MultipleStreams_ShouldMaintainSeparateCountersAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:LargeSequenceNumbers_VariousLargeValues_ShouldHandleCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:MultipleStreams_ManyKeys_ShouldMaintainSeparatelyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ResetDuringConcurrentAccess_ShouldNotCorruptAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:NegativeResetValue_ShouldWorkCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:SequentialAccess_VariousCallCounts_ShouldCompleteQuicklyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ConcurrentAccess_ManyStreams_ShouldDistributeEvenlyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:CancellationToken_Cancelled_ShouldThrowAsync</tests>
  public Task<long> GetNextAsync(string streamKey, CancellationToken ct = default) {
    ct.ThrowIfCancellationRequested();

    // GetOrAdd is thread-safe: if multiple threads call this simultaneously,
    // only one will create the counter, others will get the same instance
    var counter = _sequences.GetOrAdd(streamKey, _ => new SequenceCounter());

    // Interlocked.Increment is atomic: -1 -> 0, 0 -> 1, 1 -> 2, etc.
    // Multiple threads incrementing the same counter will each get a unique value
    var next = Interlocked.Increment(ref counter.Value);

    return Task.FromResult(next);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:LargeSequenceNumbers_VariousLargeValues_ShouldHandleCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:SequentialAccess_VariousCallCounts_ShouldCompleteQuicklyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ConcurrentAccess_ManyStreams_ShouldDistributeEvenlyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:UnusedStreams_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:GetCurrent_AfterMultipleCalls_ShouldReturnLatestAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ResetDuringConcurrentAccess_ShouldNotCorruptAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:CancellationToken_Cancelled_ShouldThrowAsync</tests>
  public Task<long> GetCurrentAsync(string streamKey, CancellationToken ct = default) {
    ct.ThrowIfCancellationRequested();

    // Read current value without incrementing
    // Interlocked.Read ensures we get a consistent 64-bit value on 32-bit systems
    if (_sequences.TryGetValue(streamKey, out var counter)) {
      return Task.FromResult(Interlocked.Read(ref counter.Value));
    }

    // Stream not yet initialized (no GetNext calls)
    return Task.FromResult(-1L);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:MixedOperations_GetNextAndReset_ShouldBeThreadSafeAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:LargeSequenceNumbers_VariousLargeValues_ShouldHandleCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:ResetDuringConcurrentAccess_ShouldNotCorruptAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:NegativeResetValue_ShouldWorkCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Sequencing.Tests/InMemorySequenceProviderTests.cs:CancellationToken_Cancelled_ShouldThrowAsync</tests>
  public Task ResetAsync(string streamKey, long newValue = 0, CancellationToken ct = default) {
    ct.ThrowIfCancellationRequested();

    // Set to newValue - 1 so next GetNext increment returns newValue
    var counter = _sequences.GetOrAdd(streamKey, _ => new SequenceCounter());
    Interlocked.Exchange(ref counter.Value, newValue - 1);

    return Task.CompletedTask;
  }
}
