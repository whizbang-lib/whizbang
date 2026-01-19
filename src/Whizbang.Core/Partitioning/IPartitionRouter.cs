using Whizbang.Core.Policies;

namespace Whizbang.Core.Partitioning;

/// <summary>
/// Routes messages to partitions based on stream key.
/// Implementations must be deterministic - same input always produces same output.
/// </summary>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs</tests>
/// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs</tests>
public interface IPartitionRouter {
  /// <summary>
  /// Determines which partition (0 to partitionCount-1) a message should be routed to.
  /// Must be deterministic and thread-safe.
  /// </summary>
  /// <param name="streamKey">The stream identifier (e.g., "order-123", aggregate ID)</param>
  /// <param name="partitionCount">Total number of partitions available</param>
  /// <param name="context">Policy context with message and runtime information</param>
  /// <returns>Partition index from 0 to partitionCount-1</returns>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs:SelectPartition_WithSinglePartition_ShouldAlwaysReturnZeroAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs:SelectPartition_ShouldReturnValidPartitionIndexAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs:SelectPartition_SameStreamKey_ShouldReturnSamePartitionAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs:SelectPartition_DifferentStreamKeys_ShouldDistributeEvenly</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs:SelectPartition_WithTwoPartitions_ShouldUseBothPartitionsAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs:SelectPartition_EmptyStreamKey_ShouldNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs:SelectPartition_NullStreamKey_ShouldNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs:SelectPartition_LargePartitionCount_ShouldHandleCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:HashAlgorithm_SameKey_AlwaysProducesSamePartitionAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:HashAlgorithm_DifferentKeys_ProduceDifferentPartitionsAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:HashAlgorithm_UnicodeKeys_HandledCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:HashAlgorithm_SimilarKeys_ProduceDifferentHashesAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:Distribution_10kStreams_DistributesEvenlyAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:Distribution_VaryingPartitionCounts_MaintainsConsistencyAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:Distribution_AllPartitionsReachable_With1000StreamsAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:EdgeCase_SpecialKeys_HandlesGracefullyAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:EdgeCase_SinglePartition_AlwaysReturnsZeroAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:EdgeCase_InvalidPartitionCount_ThrowsArgumentOutOfRangeAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:EdgeCase_VaryingKeyLengths_HandlesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:EdgeCase_UnicodeKeys_HandlesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:Performance_1MillionRoutes_CompletesQuicklyAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:Performance_VariousKeySizes_HandlesEfficientlyAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs:Performance_ConcurrentRouting_ThreadSafeAsync</tests>
  int SelectPartition(string streamKey, int partitionCount, PolicyContext context);
}
