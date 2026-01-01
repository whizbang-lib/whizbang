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
  /// <param name="testIndex">The fixed test index (0-based) assigned to this test class.</param>
  public static async Task<ServiceBusBatchFixture> GetBatchFixtureAsync(int testIndex) {
    // Determine which batch this test belongs to
    var batchIndex = testIndex / TESTS_PER_BATCH;

    Console.WriteLine($"[SharedFixture] Test index {testIndex} assigned to Batch {batchIndex}");

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
