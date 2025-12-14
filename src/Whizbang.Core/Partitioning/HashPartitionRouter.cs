using System.Buffers;
using System.Text;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Partitioning;

/// <summary>
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
/// Routes messages to partitions using consistent hashing based on stream key.
/// Provides stable, deterministic routing - same stream key always goes to same partition.
/// Uses FNV-1a hash algorithm for fast, well-distributed hashing.
/// </summary>
public class HashPartitionRouter : IPartitionRouter {
  /// <inheritdoc />
  public int SelectPartition(string streamKey, int partitionCount, PolicyContext context) {
    // Handle edge cases
    if (partitionCount <= 0) {
      throw new ArgumentOutOfRangeException(nameof(partitionCount), "Partition count must be greater than zero");
    }

    if (partitionCount == 1) {
      return 0; // Only one partition available
    }

    // Handle null or empty stream key
    if (string.IsNullOrEmpty(streamKey)) {
      return 0; // Route null/empty to partition 0
    }

    // Compute hash using FNV-1a algorithm (fast and well-distributed)
    var hash = ComputeFnv1aHash(streamKey);

    // Use modulo to map hash to partition index
    // Take absolute value to handle negative hash values
    var partition = Math.Abs(hash % partitionCount);

    return partition;
  }

  /// <summary>
  /// Computes FNV-1a hash of a string with zero heap allocations.
  /// FNV-1a (Fowler-Noll-Vo) is a fast, non-cryptographic hash function with good distribution.
  /// Uses stackalloc for small strings and ArrayPool for large strings to avoid allocations.
  /// </summary>
  private static int ComputeFnv1aHash(string value) {
    // FNV-1a parameters for 32-bit hash
    const int FNV_PRIME = 16777619;
    const int FNV_OFFSET_BASIS = unchecked((int)2166136261);
    const int STACK_ALLOC_THRESHOLD = 256; // Use stack for strings up to 256 bytes encoded

    var hash = FNV_OFFSET_BASIS;
    var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);

    if (maxBytes <= STACK_ALLOC_THRESHOLD) {
      // Stack allocation for small strings (ZERO heap allocation)
      Span<byte> bytes = stackalloc byte[maxBytes];
      var actualBytes = Encoding.UTF8.GetBytes(value, bytes);

      for (int i = 0; i < actualBytes; i++) {
        hash ^= bytes[i];
        hash *= FNV_PRIME;
      }
    } else {
      // Rent from ArrayPool for large strings (minimal allocation, reused)
      var buffer = ArrayPool<byte>.Shared.Rent(maxBytes);
      try {
        var actualBytes = Encoding.UTF8.GetBytes(value, buffer);
        for (int i = 0; i < actualBytes; i++) {
          hash ^= buffer[i];
          hash *= FNV_PRIME;
        }
      } finally {
        ArrayPool<byte>.Shared.Return(buffer);
      }
    }

    return hash;
  }
}
