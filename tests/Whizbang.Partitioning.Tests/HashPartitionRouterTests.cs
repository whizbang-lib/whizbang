using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Partitioning;
using Whizbang.Core.Policies;

namespace Whizbang.Partitioning.Tests;

/// <summary>
/// Tests for HashPartitionRouter implementation.
/// Inherits contract tests to ensure compliance with IPartitionRouter requirements.
/// </summary>
[Category("Partitioning")]
[InheritsTests]
public class HashPartitionRouterTests : PartitionRouterContractTests {
  /// <summary>
  /// Creates a HashPartitionRouter for testing.
  /// </summary>
  protected override IPartitionRouter CreateRouter() {
    return new HashPartitionRouter();
  }

  // ==================== HASH ALGORITHM TESTS ====================

  [Test]
  public async Task HashAlgorithm_SameKey_AlwaysProducesSamePartitionAsync() {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var streamKey = "test-stream-123";
    var partitionCount = 10;

    // Act - Call multiple times with same key
    var partition1 = router.SelectPartition(streamKey, partitionCount, context);
    var partition2 = router.SelectPartition(streamKey, partitionCount, context);
    var partition3 = router.SelectPartition(streamKey, partitionCount, context);

    // Assert - Should always return same partition (deterministic)
    await Assert.That(partition1).IsEqualTo(partition2);
    await Assert.That(partition2).IsEqualTo(partition3);
  }

  [Test]
  public async Task HashAlgorithm_DifferentKeys_ProduceDifferentPartitionsAsync() {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 100;

    // Act - Test 1000 different stream keys
    var partitions = new HashSet<int>();
    for (int i = 0; i < 1000; i++) {
      var streamKey = $"stream-{i}";
      var partition = router.SelectPartition(streamKey, partitionCount, context);
      partitions.Add(partition);
    }

    // Assert - Should use at least 90% of available partitions (good distribution)
    await Assert.That(partitions.Count).IsGreaterThanOrEqualTo(90);
  }

  [Test]
  [MethodDataSource(nameof(GetHashAlgorithmUnicodeKeys))]
  public async Task HashAlgorithm_UnicodeKeys_HandledCorrectlyAsync(string key, string description) {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 10;

    // Act
    var partition = router.SelectPartition(key, partitionCount, context);

    // Assert - Should return valid partition
    await Assert.That(partition).IsGreaterThanOrEqualTo(0).And.IsLessThan(partitionCount);

    // Should be deterministic
    var partition2 = router.SelectPartition(key, partitionCount, context);
    await Assert.That(partition2).IsEqualTo(partition);
  }

  public static IEnumerable<(string key, string description)> GetHashAlgorithmUnicodeKeys() {
    yield return ("stream-😀-🎉-🚀", "Emoji stream");
    yield return ("流-键-测试", "Chinese characters");
    yield return ("مفتاح-البث", "Arabic characters");
    yield return ("stream-混合-مفتاح-😀", "Mixed Unicode");
    yield return ("Привет-мир", "Cyrillic");
    yield return ("สวัสดี", "Thai");
    yield return ("🏳️‍🌈🏴‍☠️", "Complex emoji sequences");
  }

  [Test]
  public async Task HashAlgorithm_SimilarKeys_ProduceDifferentHashesAsync() {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 100;

    // Act - Test keys that differ by one character
    var partitions = new HashSet<int> {
      router.SelectPartition("user-1000", partitionCount, context),
      router.SelectPartition("user-1001", partitionCount, context),
      router.SelectPartition("user-1002", partitionCount, context),
      router.SelectPartition("user-1003", partitionCount, context),
      router.SelectPartition("user-1004", partitionCount, context)
    };

    // Assert - Even similar keys should distribute across partitions
    // (Not guaranteed to be all unique, but should be > 1)
    await Assert.That(partitions.Count).IsGreaterThan(1);
  }

  // ==================== PARTITION DISTRIBUTION TESTS ====================

  [Test]
  public async Task Distribution_10kStreams_DistributesEvenlyAsync() {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 10;
    var streamCount = 10_000;
    var partitionCounts = new int[partitionCount];

    // Act - Route 10k streams and count distribution
    for (int i = 0; i < streamCount; i++) {
      var streamKey = $"stream-{i}";
      var partition = router.SelectPartition(streamKey, partitionCount, context);
      partitionCounts[partition]++;
    }

    // Assert - Each partition should get roughly equal share (±20%)
    var expectedPerPartition = streamCount / partitionCount; // 1000
    var tolerance = expectedPerPartition * 0.20; // ±200

    for (int i = 0; i < partitionCount; i++) {
      var count = partitionCounts[i];
      await Assert.That(count).IsGreaterThanOrEqualTo((int)(expectedPerPartition - tolerance));
      await Assert.That(count).IsLessThanOrEqualTo((int)(expectedPerPartition + tolerance));
    }
  }

  [Test]
  [Arguments(1)]
  [Arguments(2)]
  [Arguments(5)]
  [Arguments(10)]
  [Arguments(50)]
  [Arguments(100)]
  [Arguments(500)]
  [Arguments(1000)]
  public async Task Distribution_VaryingPartitionCounts_MaintainsConsistencyAsync(int partitionCount) {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var streamKey = "consistent-stream-key";

    // Act - Get partition for this partition count
    var partition = router.SelectPartition(streamKey, partitionCount, context);

    // Assert - Same key should produce valid partition for any count
    await Assert.That(partition).IsGreaterThanOrEqualTo(0).And.IsLessThan(partitionCount);

    // Verify determinism
    var partition2 = router.SelectPartition(streamKey, partitionCount, context);
    await Assert.That(partition2).IsEqualTo(partition);
  }

  [Test]
  public async Task Distribution_AllPartitionsReachable_With1000StreamsAsync() {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 20;
    var usedPartitions = new HashSet<int>();

    // Act - Route 1000 streams
    for (int i = 0; i < 1000; i++) {
      var streamKey = $"stream-{i}";
      var partition = router.SelectPartition(streamKey, partitionCount, context);
      usedPartitions.Add(partition);
    }

    // Assert - All partitions should be reachable with enough streams
    await Assert.That(usedPartitions.Count).IsEqualTo(partitionCount);
  }

  // ==================== EDGE CASE TESTS ====================

  [Test]
  [Arguments(null, "null")]
  [Arguments("", "empty")]
  [Arguments("   ", "whitespace")]
  public async Task EdgeCase_SpecialKeys_HandlesGracefullyAsync(string? streamKey, string description) {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 10;

    // Act
    var partition = router.SelectPartition(streamKey!, partitionCount, context);

    // Assert - Should return valid partition (0 for null/empty, any valid for whitespace)
    await Assert.That(partition).IsGreaterThanOrEqualTo(0).And.IsLessThan(partitionCount);

    // Verify determinism
    var partition2 = router.SelectPartition(streamKey!, partitionCount, context);
    await Assert.That(partition2).IsEqualTo(partition);
  }

  [Test]
  public async Task EdgeCase_SinglePartition_AlwaysReturnsZeroAsync() {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();

    // Act - Try many different stream keys
    var results = new HashSet<int>();
    for (int i = 0; i < 100; i++) {
      var partition = router.SelectPartition($"stream-{i}", 1, context);
      results.Add(partition);
    }

    // Assert - Should always return 0 when only one partition
    await Assert.That(results).HasCount().EqualTo(1);
    await Assert.That(results.First()).IsEqualTo(0);
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  [Arguments(-5)]
  [Arguments(-100)]
  public async Task EdgeCase_InvalidPartitionCount_ThrowsArgumentOutOfRangeAsync(int invalidCount) {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
        () => Task.FromResult(router.SelectPartition("test", invalidCount, context))
    );
  }

  [Test]
  [Arguments(3, "short ASCII")]
  [Arguments(100, "medium ASCII")]
  [Arguments(500, "long ASCII (ArrayPool)")]
  [Arguments(1000, "very long ASCII (ArrayPool)")]
  public async Task EdgeCase_VaryingKeyLengths_HandlesCorrectlyAsync(int keyLength, string description) {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 10;
    var testKey = new string('x', keyLength);

    // Act
    var partition = router.SelectPartition(testKey, partitionCount, context);

    // Assert - Should handle without error and return valid partition
    await Assert.That(partition).IsGreaterThanOrEqualTo(0).And.IsLessThan(partitionCount);

    // Should be deterministic
    var partition2 = router.SelectPartition(testKey, partitionCount, context);
    await Assert.That(partition2).IsEqualTo(partition);
  }

  [Test]
  [MethodDataSource(nameof(GetUnicodeTestKeys))]
  public async Task EdgeCase_UnicodeKeys_HandlesCorrectlyAsync(string key, string description) {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 10;

    // Act
    var partition = router.SelectPartition(key, partitionCount, context);

    // Assert - Should handle without error
    await Assert.That(partition).IsGreaterThanOrEqualTo(0).And.IsLessThan(partitionCount);

    // Should be deterministic
    var partition2 = router.SelectPartition(key, partitionCount, context);
    await Assert.That(partition2).IsEqualTo(partition);
  }

  public static IEnumerable<(string key, string description)> GetUnicodeTestKeys() {
    yield return ("😀🎉🚀", "Emoji");
    yield return ("流-键-测试", "Chinese");
    yield return ("مفتاح-البث", "Arabic");
    yield return ("stream-混合-مفتاح-😀", "Mixed Unicode");
    yield return (string.Concat(Enumerable.Repeat("😀", 100)), "Long Emoji (400 bytes)");
    yield return (string.Concat(Enumerable.Repeat("中", 200)), "Long Chinese (600 bytes)");
  }

  // ==================== PERFORMANCE TESTS ====================

  [Test]
  public async Task Performance_1MillionRoutes_CompletesQuicklyAsync() {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 100;
    var routeCount = 1_000_000;

    // Act
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < routeCount; i++) {
      var streamKey = $"stream-{i % 10000}"; // Cycle through 10k unique keys
      router.SelectPartition(streamKey, partitionCount, context);
    }
    stopwatch.Stop();

    // Assert - Should complete in reasonable time (< 1 second)
    await Assert.That(stopwatch.Elapsed.TotalSeconds).IsLessThan(1.0);
  }

  [Test]
  [MethodDataSource(nameof(GetPerformanceTestKeys))]
  public async Task Performance_VariousKeySizes_HandlesEfficientlyAsync(string key, string description, int iterations) {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 10;

    // Act - Route key multiple times
    for (int i = 0; i < iterations; i++) {
      var partition = router.SelectPartition(key, partitionCount, context);
      await Assert.That(partition).IsGreaterThanOrEqualTo(0).And.IsLessThan(partitionCount);
    }

    // Assert - No exceptions means the allocation path (stackalloc vs ArrayPool) works correctly
  }

  public static IEnumerable<(string key, string description, int iterations)> GetPerformanceTestKeys() {
    yield return ("a", "1 char (stackalloc)", 10000);
    yield return ("abc", "3 chars (stackalloc)", 10000);
    yield return ("test-key", "8 chars (stackalloc)", 10000);
    yield return ("user-123-stream-key", "19 chars (stackalloc)", 5000);
    yield return (new string('x', 100), "100 chars (stackalloc)", 5000);
    yield return (new string('x', 256), "256 chars (boundary)", 2000);
    yield return (new string('x', 500), "500 chars (ArrayPool)", 1000);
    yield return (new string('y', 1000), "1000 chars (ArrayPool)", 1000);
  }

  [Test]
  public async Task Performance_ConcurrentRouting_ThreadSafeAsync() {
    // Arrange
    var router = new HashPartitionRouter();
    var context = CreateTestContext();
    var partitionCount = 100;
    var taskCount = 1000;
    var tasks = new Task<int>[taskCount];

    // Act - Route concurrently from multiple threads
    for (int i = 0; i < taskCount; i++) {
      var streamKey = $"stream-{i}";
      tasks[i] = Task.Run(() => router.SelectPartition(streamKey, partitionCount, context));
    }

    var results = await Task.WhenAll(tasks);

    // Assert - All results should be valid partitions
    foreach (var partition in results) {
      await Assert.That(partition).IsGreaterThanOrEqualTo(0).And.IsLessThan(partitionCount);
    }

    // Verify determinism - same key should always route to same partition
    var testKey = "stream-42";
    var partition1 = router.SelectPartition(testKey, partitionCount, context);
    var partition2 = router.SelectPartition(testKey, partitionCount, context);
    await Assert.That(partition2).IsEqualTo(partition1);
  }
}
