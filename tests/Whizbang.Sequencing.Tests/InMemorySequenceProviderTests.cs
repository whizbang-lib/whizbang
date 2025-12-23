using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Sequencing;

namespace Whizbang.Sequencing.Tests;

/// <summary>
/// Tests for InMemorySequenceProvider implementation.
/// Inherits contract tests to ensure compliance with ISequenceProvider requirements.
/// </summary>
[Category("Sequencing")]
[InheritsTests]
public class InMemorySequenceProviderTests : SequenceProviderContractTests {
  /// <summary>
  /// Creates an InMemorySequenceProvider for testing.
  /// </summary>
  protected override ISequenceProvider CreateProvider() {
    return new InMemorySequenceProvider();
  }

  // ==================== THREAD SAFETY TESTS ====================

  [Test]
  [Arguments(10)]
  [Arguments(50)]
  [Arguments(100)]
  [Arguments(500)]
  [Arguments(1000)]
  [Arguments(5000)]
  public async Task ConcurrentAccess_VariousTaskCounts_ShouldMaintainConsistencyAsync(int taskCount) {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var streamKey = $"concurrent-stream-{taskCount}";
    var tasks = new Task<long>[taskCount];

    // Act - Launch tasks simultaneously calling GetNext
    for (int i = 0; i < taskCount; i++) {
      tasks[i] = Task.Run(async () => await provider.GetNextAsync(streamKey));
    }
    var results = await Task.WhenAll(tasks);

    // Assert - All values should be unique and in range [0, taskCount-1]
    var sortedResults = results.OrderBy(x => x).ToArray();
    await Assert.That(sortedResults).Count().IsEqualTo(taskCount);
    await Assert.That(sortedResults.Distinct()).Count().IsEqualTo(taskCount);

    // Verify no gaps in sequence (first and last values)
    await Assert.That(sortedResults[0]).IsEqualTo(0);
    await Assert.That(sortedResults[taskCount - 1]).IsEqualTo(taskCount - 1);
  }

  [Test]
  public async Task MixedOperations_GetNextAndReset_ShouldBeThreadSafeAsync() {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var streamKey = "mixed-ops-stream";
    var getNextCount = 50;
    var getTasks = new Task<long>[getNextCount];

    // Act - Mix GetNext calls with a Reset in the middle
    for (int i = 0; i < getNextCount / 2; i++) {
      getTasks[i] = provider.GetNextAsync(streamKey);
    }

    // Reset to 1000 in the middle
    await provider.ResetAsync(streamKey, 1000);

    for (int i = getNextCount / 2; i < getNextCount; i++) {
      getTasks[i] = provider.GetNextAsync(streamKey);
    }

    var results = await Task.WhenAll(getTasks);

    // Assert - All values should be unique (no duplicates despite reset)
    await Assert.That(results.Distinct()).Count().IsEqualTo(getNextCount);

    // Some values should be in [0, 24] range, others in [1000+] range
    var lowValues = results.Where(x => x < 100).ToArray();
    var highValues = results.Where(x => x >= 1000).ToArray();

    await Assert.That(lowValues.Length + highValues.Length).IsEqualTo(getNextCount);
  }

  [Test]
  [Arguments(5, 10)]
  [Arguments(10, 50)]
  [Arguments(20, 100)]
  [Arguments(50, 200)]
  public async Task ConcurrentAccess_MultipleStreams_ShouldMaintainSeparateCountersAsync(int streamCount, int callsPerStream) {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var allTasks = new List<Task<(string stream, long value)>>();

    // Act - Call GetNext concurrently across multiple streams
    for (int streamIdx = 0; streamIdx < streamCount; streamIdx++) {
      var streamKey = $"stream-{streamIdx}";
      for (int call = 0; call < callsPerStream; call++) {
        var capturedStreamKey = streamKey;
        allTasks.Add(Task.Run(async () => {
          var value = await provider.GetNextAsync(capturedStreamKey);
          return (capturedStreamKey, value);
        }));
      }
    }

    var results = await Task.WhenAll(allTasks);

    // Assert - Each stream should have unique values in range [0, callsPerStream-1]
    for (int streamIdx = 0; streamIdx < streamCount; streamIdx++) {
      var streamKey = $"stream-{streamIdx}";
      var streamResults = results
          .Where(r => r.stream == streamKey)
          .Select(r => r.value)
          .OrderBy(v => v)
          .ToArray();

      await Assert.That(streamResults).Count().IsEqualTo(callsPerStream);
      await Assert.That(streamResults.Distinct()).Count().IsEqualTo(callsPerStream);
      await Assert.That(streamResults.Min()).IsEqualTo(0);
      await Assert.That(streamResults.Max()).IsEqualTo(callsPerStream - 1);
    }
  }

  // ==================== EDGE CASE TESTS ====================

  [Test]
  [MethodDataSource(nameof(GetLargeSequenceValues))]
  public async Task LargeSequenceNumbers_VariousLargeValues_ShouldHandleCorrectlyAsync(long startValue, string description) {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var streamKey = $"large-sequence-{description}";

    // Act - Reset to large value and get next values
    await provider.ResetAsync(streamKey, startValue);
    var first = await provider.GetNextAsync(streamKey);
    var second = await provider.GetNextAsync(streamKey);
    var current = await provider.GetCurrentAsync(streamKey);

    // Assert
    await Assert.That(first).IsEqualTo(startValue);
    await Assert.That(second).IsEqualTo(startValue + 1);
    await Assert.That(current).IsEqualTo(startValue + 1);
  }

  public static IEnumerable<(long startValue, string description)> GetLargeSequenceValues() {
    yield return (long.MaxValue - 100, "near-max-100");
    yield return (long.MaxValue - 1000, "near-max-1000");
    yield return (long.MaxValue / 2, "half-max");
    yield return (1_000_000_000_000L, "trillion");
    yield return (9_000_000_000_000_000_000L, "nine-quintillion");
  }

  [Test]
  [Arguments(10)]
  [Arguments(100)]
  [Arguments(500)]
  [Arguments(1000)]
  [Arguments(5000)]
  public async Task MultipleStreams_ManyKeys_ShouldMaintainSeparatelyAsync(int streamCount) {
    // Arrange
    var provider = new InMemorySequenceProvider();

    // Act - Create many streams and get first sequence number
    var tasks = new Task<long>[streamCount];
    for (int i = 0; i < streamCount; i++) {
      var streamKey = $"stream-{i}";
      tasks[i] = provider.GetNextAsync(streamKey);
    }
    var results = await Task.WhenAll(tasks);

    // Assert - Each stream should start at 0
    foreach (var result in results) {
      await Assert.That(result).IsEqualTo(0);
    }

    // Verify each stream maintains its own counter
    var randomStreamKey = $"stream-{streamCount / 2}";
    var nextValue = await provider.GetNextAsync(randomStreamKey);
    await Assert.That(nextValue).IsEqualTo(1); // Should be 1 (second call for this stream)
  }

  [Test]
  public async Task ResetDuringConcurrentAccess_ShouldNotCorruptAsync() {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var streamKey = "reset-concurrent-stream";
    var callCount = 100;
    var allTasks = new List<Task>();

    // Act - Mix GetNext and Reset calls concurrently
    for (int i = 0; i < callCount; i++) {
      allTasks.Add(provider.GetNextAsync(streamKey));

      if (i % 20 == 0) {
        var resetValue = i * 10;
        allTasks.Add(provider.ResetAsync(streamKey, resetValue));
      }
    }

    await Task.WhenAll(allTasks);

    // Assert - Stream should still be operational
    var finalValue = await provider.GetNextAsync(streamKey);
    await Assert.That(finalValue).IsGreaterThanOrEqualTo(0); // Should be some valid value

    // GetCurrent should also work
    var currentValue = await provider.GetCurrentAsync(streamKey);
    await Assert.That(currentValue).IsEqualTo(finalValue);
  }

  [Test]
  [Arguments(-1)]
  [Arguments(-10)]
  [Arguments(-100)]
  [Arguments(-1000)]
  [Arguments(long.MinValue + 100)]
  public async Task NegativeResetValue_ShouldWorkCorrectlyAsync(long negativeValue) {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var streamKey = $"negative-reset-{negativeValue}";

    // Act - Reset to negative value
    await provider.ResetAsync(streamKey, negativeValue);
    var first = await provider.GetNextAsync(streamKey);
    var second = await provider.GetNextAsync(streamKey);

    // Assert
    await Assert.That(first).IsEqualTo(negativeValue);
    await Assert.That(second).IsEqualTo(negativeValue + 1);
  }

  // ==================== PERFORMANCE TESTS ====================

  [Test]
  [Arguments(1_000, 1.0)]
  [Arguments(10_000, 2.0)]
  [Arguments(100_000, 3.0)]
  [Arguments(1_000_000, 5.0)]
  public async Task SequentialAccess_VariousCallCounts_ShouldCompleteQuicklyAsync(int callCount, double maxSeconds) {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var streamKey = $"perf-stream-{callCount}";

    // Act
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < callCount; i++) {
      await provider.GetNextAsync(streamKey);
    }
    stopwatch.Stop();

    // Assert - Should complete in reasonable time
    await Assert.That(stopwatch.Elapsed.TotalSeconds).IsLessThan(maxSeconds);

    // Verify final value
    var finalValue = await provider.GetCurrentAsync(streamKey);
    await Assert.That(finalValue).IsEqualTo(callCount - 1);
  }

  [Test]
  [Arguments(10, 100, 5.0)]
  [Arguments(50, 500, 8.0)]
  [Arguments(100, 1000, 10.0)]
  public async Task ConcurrentAccess_ManyStreams_ShouldDistributeEvenlyAsync(int streamCount, int callsPerStream, double maxSeconds) {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var allTasks = new List<Task<long>>();

    // Act - Concurrent calls across many streams
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    for (int streamIdx = 0; streamIdx < streamCount; streamIdx++) {
      var streamKey = $"concurrent-stream-{streamIdx}";
      for (int call = 0; call < callsPerStream; call++) {
        var capturedStreamKey = streamKey;
        allTasks.Add(Task.Run(async () => await provider.GetNextAsync(capturedStreamKey)));
      }
    }

    await Task.WhenAll(allTasks);
    stopwatch.Stop();

    // Assert - Should complete in reasonable time
    await Assert.That(stopwatch.Elapsed.TotalSeconds).IsLessThan(maxSeconds);

    // Verify each stream reached expected count
    for (int streamIdx = 0; streamIdx < streamCount; streamIdx++) {
      var streamKey = $"concurrent-stream-{streamIdx}";
      var current = await provider.GetCurrentAsync(streamKey);
      await Assert.That(current).IsEqualTo(callsPerStream - 1);
    }
  }

  // ==================== MEMORY/BEHAVIOR TESTS ====================

  [Test]
  public async Task UnusedStreams_ShouldReturnMinusOneAsync() {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var neverUsedStreamKey = "never-used-stream";

    // Act - Get current for a stream that was never initialized
    var current = await provider.GetCurrentAsync(neverUsedStreamKey);

    // Assert
    await Assert.That(current).IsEqualTo(-1);
  }

  [Test]
  public async Task GetCurrent_AfterMultipleCalls_ShouldReturnLatestAsync() {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var streamKey = "get-current-test-stream";

    // Act
    await provider.GetNextAsync(streamKey); // 0
    await provider.GetNextAsync(streamKey); // 1
    await provider.GetNextAsync(streamKey); // 2
    var current = await provider.GetCurrentAsync(streamKey);

    // Assert
    await Assert.That(current).IsEqualTo(2);
  }

  [Test]
  public async Task CancellationToken_Cancelled_ShouldThrowAsync() {
    // Arrange
    var provider = new InMemorySequenceProvider();
    var streamKey = "cancellation-test-stream";
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(
        async () => await provider.GetNextAsync(streamKey, cts.Token)
    );

    await Assert.ThrowsAsync<OperationCanceledException>(
        async () => await provider.GetCurrentAsync(streamKey, cts.Token)
    );

    await Assert.ThrowsAsync<OperationCanceledException>(
        async () => await provider.ResetAsync(streamKey, 0, cts.Token)
    );
  }
}
