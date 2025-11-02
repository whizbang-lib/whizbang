using Whizbang.Core.Policies;

namespace Whizbang.Core.Partitioning;

/// <summary>
/// Routes messages to partitions based on stream key.
/// Implementations must be deterministic - same input always produces same output.
/// </summary>
public interface IPartitionRouter {
  /// <summary>
  /// Determines which partition (0 to partitionCount-1) a message should be routed to.
  /// Must be deterministic and thread-safe.
  /// </summary>
  /// <param name="streamKey">The stream identifier (e.g., "order-123", aggregate ID)</param>
  /// <param name="partitionCount">Total number of partitions available</param>
  /// <param name="context">Policy context with message and runtime information</param>
  /// <returns>Partition index from 0 to partitionCount-1</returns>
  int SelectPartition(string streamKey, int partitionCount, PolicyContext context);
}
