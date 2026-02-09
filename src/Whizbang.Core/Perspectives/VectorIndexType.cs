namespace Whizbang.Core.Perspectives;

/// <summary>
/// Index types for vector columns in pgvector.
/// Each type offers different trade-offs between build time, memory, and query performance.
/// </summary>
/// <docs>perspectives/vector-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/VectorIndexTypeTests.cs</tests>
public enum VectorIndexType {
  /// <summary>
  /// No index (exact search). Use for small datasets only.
  /// Performs full table scan for each query.
  /// </summary>
  None = 0,

  /// <summary>
  /// IVFFlat (Inverted File Flat) index.
  /// Good balance of build speed and query performance.
  /// Requires setting the number of lists (partitions) via IndexLists parameter.
  /// Lower recall than HNSW but faster build time and less memory.
  /// </summary>
  IVFFlat = 1,

  /// <summary>
  /// HNSW (Hierarchical Navigable Small World) index.
  /// Better recall and query performance than IVFFlat.
  /// Slower build time and higher memory usage.
  /// Recommended for production workloads with large datasets.
  /// </summary>
  HNSW = 2
}
