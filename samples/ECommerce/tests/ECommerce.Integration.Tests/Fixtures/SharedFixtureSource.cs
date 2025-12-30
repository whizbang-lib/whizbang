using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using TUnit.Core;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Provides batch-aware Service Bus emulator management for parallel test execution.
/// Each batch of 25 tests gets its own emulator instance on a unique port.
/// Tests within a batch run in PARALLEL and are isolated by dedicated topic sets.
/// </summary>
public static class SharedFixtureSource {
  private static readonly ConcurrentDictionary<int, Lazy<Task<ServiceBusBatchFixture>>> _batchFixtures = new();
  private const int TESTS_PER_BATCH = 25;  // Each batch supports up to 25 tests

  /// <summary>
  /// Gets or initializes the batch-specific ServiceBus emulator fixture.
  /// Each batch of 25 tests shares one emulator instance.
  /// </summary>
  public static async Task<ServiceBusBatchFixture> GetBatchFixtureAsync(Type testClassType) {
    // Determine which batch this test belongs to
    var testIndex = GetTestIndex(testClassType); // 0-based index
    var batchIndex = testIndex / TESTS_PER_BATCH;

    Console.WriteLine($"[SharedFixture] Test class '{testClassType.Name}' assigned index {testIndex} → Batch {batchIndex}");

    // Get or create fixture for this batch
    var lazyFixture = _batchFixtures.GetOrAdd(batchIndex,
      idx => new Lazy<Task<ServiceBusBatchFixture>>(async () => {
        var fixture = new ServiceBusBatchFixture(idx);
        await fixture.InitializeAsync();
        return fixture;
      }));

    return await lazyFixture.Value;
  }

  /// <summary>
  /// Calculates a stable test index from the test class type.
  /// Uses hash of test class name to ensure consistent batching across runs.
  /// </summary>
  private static int GetTestIndex(Type testClassType) {
    // Assign stable index based on test class name
    var testClassName = testClassType.FullName ?? testClassType.Name;

    // Use stable hash to ensure consistent batching across runs
    // Support up to 1000 tests (40 batches of 25 tests each)
    return Math.Abs(testClassName.GetHashCode()) % 1000;
  }

  /// <summary>
  /// Final cleanup: disposes all batch fixtures when tests complete.
  /// </summary>
  public static async Task DisposeAsync() {
    foreach (var lazyFixture in _batchFixtures.Values) {
      if (lazyFixture.IsValueCreated) {
        var fixture = await lazyFixture.Value;
        await fixture.DisposeAsync();
      }
    }

    _batchFixtures.Clear();
    Console.WriteLine("[SharedFixture] All batch fixtures disposed.");
  }
}
