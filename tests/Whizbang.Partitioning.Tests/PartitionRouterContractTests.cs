using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Partitioning;
using Whizbang.Core.Policies;

namespace Whizbang.Partitioning.Tests;

/// <summary>
/// Contract tests that all IPartitionRouter implementations must pass.
/// These tests define the required behavior for partition routing.
/// </summary>
[Category("Partitioning")]
public abstract class PartitionRouterContractTests {
  /// <summary>
  /// Factory method that derived classes must implement to provide their specific implementation.
  /// </summary>
  protected abstract IPartitionRouter CreateRouter();

  /// <summary>
  /// Helper to create a minimal PolicyContext for testing.
  /// Partition routing doesn't use context currently, so we pass null for simplicity.
  /// </summary>
  protected static PolicyContext CreateTestContext() {
    return null!; // Partition routers don't currently use PolicyContext
  }

  [Test]
  public async Task SelectPartition_WithSinglePartition_ShouldAlwaysReturnZeroAsync() {
    // Arrange
    var router = CreateRouter();
    var context = CreateTestContext();

    // Act
    var partition1 = router.SelectPartition("stream-1", 1, context);
    var partition2 = router.SelectPartition("stream-2", 1, context);
    var partition3 = router.SelectPartition("stream-3", 1, context);

    // Assert - Only partition 0 exists
    await Assert.That(partition1).IsEqualTo(0);
    await Assert.That(partition2).IsEqualTo(0);
    await Assert.That(partition3).IsEqualTo(0);
  }

  [Test]
  public async Task SelectPartition_ShouldReturnValidPartitionIndexAsync() {
    // Arrange
    var router = CreateRouter();
    var context = CreateTestContext();
    var partitionCount = 10;

    // Act & Assert - Test multiple stream keys
    for (int i = 0; i < 100; i++) {
      var streamKey = $"stream-{i}";
      var partition = router.SelectPartition(streamKey, partitionCount, context);

      await Assert.That(partition).IsGreaterThanOrEqualTo(0);
      await Assert.That(partition).IsLessThan(partitionCount);
    }
  }

  [Test]
  public async Task SelectPartition_SameStreamKey_ShouldReturnSamePartitionAsync() {
    // Arrange
    var router = CreateRouter();
    var context = CreateTestContext();
    var streamKey = "order-12345";
    var partitionCount = 10;

    // Act - Call multiple times with same stream key
    var partition1 = router.SelectPartition(streamKey, partitionCount, context);
    var partition2 = router.SelectPartition(streamKey, partitionCount, context);
    var partition3 = router.SelectPartition(streamKey, partitionCount, context);

    // Assert - Should be deterministic
    await Assert.That(partition1).IsEqualTo(partition2);
    await Assert.That(partition2).IsEqualTo(partition3);
  }

  [Test]
  public async Task SelectPartition_DifferentStreamKeys_ShouldDistributeEvenly() {
    // Arrange
    var router = CreateRouter();
    var context = CreateTestContext();
    var partitionCount = 4;
    var streamCount = 1000;
    var partitionCounts = new int[partitionCount];

    // Act - Route 1000 different streams
    for (int i = 0; i < streamCount; i++) {
      var streamKey = $"stream-{i}";
      var partition = router.SelectPartition(streamKey, partitionCount, context);
      partitionCounts[partition]++;
    }

    // Assert - Each partition should have roughly 250 streams (1000 / 4)
    // Allow for some variance (150-350 per partition is reasonable)
    foreach (var count in partitionCounts) {
      await Assert.That(count).IsGreaterThan(150);
      await Assert.That(count).IsLessThan(350);
    }
  }

  [Test]
  public async Task SelectPartition_WithTwoPartitions_ShouldUseBothPartitionsAsync() {
    // Arrange
    var router = CreateRouter();
    var context = CreateTestContext();
    var partitionCount = 2;
    var partition0Used = false;
    var partition1Used = false;

    // Act - Try up to 100 stream keys
    for (int i = 0; i < 100 && (!partition0Used || !partition1Used); i++) {
      var partition = router.SelectPartition($"stream-{i}", partitionCount, context);
      if (partition == 0) {
        partition0Used = true;
      }

      if (partition == 1) {
        partition1Used = true;
      }
    }

    // Assert - Both partitions should be used
    await Assert.That(partition0Used).IsTrue();
    await Assert.That(partition1Used).IsTrue();
  }

  [Test]
  public async Task SelectPartition_EmptyStreamKey_ShouldNotThrowAsync() {
    // Arrange
    var router = CreateRouter();
    var context = CreateTestContext();

    // Act
    var partition = router.SelectPartition("", 4, context);

    // Assert - Should handle empty string gracefully
    await Assert.That(partition).IsGreaterThanOrEqualTo(0);
    await Assert.That(partition).IsLessThan(4);
  }

  [Test]
  public async Task SelectPartition_NullStreamKey_ShouldNotThrowAsync() {
    // Arrange
    var router = CreateRouter();
    var context = CreateTestContext();

    // Act
    var partition = router.SelectPartition(null!, 4, context);

    // Assert - Should handle null gracefully
    await Assert.That(partition).IsGreaterThanOrEqualTo(0);
    await Assert.That(partition).IsLessThan(4);
  }

  [Test]
  public async Task SelectPartition_LargePartitionCount_ShouldHandleCorrectlyAsync() {
    // Arrange
    var router = CreateRouter();
    var context = CreateTestContext();
    var partitionCount = 1000;

    // Act
    var partition = router.SelectPartition("test-stream", partitionCount, context);

    // Assert
    await Assert.That(partition).IsGreaterThanOrEqualTo(0);
    await Assert.That(partition).IsLessThan(partitionCount);
  }
}
