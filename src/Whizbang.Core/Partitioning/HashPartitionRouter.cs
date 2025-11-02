using System.Text;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Partitioning;

/// <summary>
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
  /// Computes FNV-1a hash of a string.
  /// FNV-1a (Fowler-Noll-Vo) is a fast, non-cryptographic hash function with good distribution.
  /// </summary>
  private static int ComputeFnv1aHash(string value) {
    // FNV-1a parameters for 32-bit hash
    const int FNV_PRIME = 16777619;
    const int FNV_OFFSET_BASIS = unchecked((int)2166136261);

    var hash = FNV_OFFSET_BASIS;
    var bytes = Encoding.UTF8.GetBytes(value);

    foreach (var b in bytes) {
      hash ^= b;
      hash *= FNV_PRIME;
    }

    return hash;
  }
}
