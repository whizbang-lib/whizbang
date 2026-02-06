namespace Whizbang.Core.Perspectives;

/// <summary>
/// Distance metrics for vector similarity search using pgvector.
/// Each metric corresponds to a PostgreSQL operator for ordering by similarity.
/// </summary>
/// <docs>perspectives/vector-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/VectorDistanceMetricTests.cs</tests>
public enum VectorDistanceMetric {
  /// <summary>
  /// L2 (Euclidean) distance. Lower values indicate more similar vectors.
  /// PostgreSQL operator: <![CDATA[<->]]>
  /// Formula: sqrt(sum((a[i] - b[i])^2))
  /// </summary>
  L2 = 0,

  /// <summary>
  /// Inner product (negative). Higher values indicate more similar vectors.
  /// PostgreSQL operator: <![CDATA[<#>]]>
  /// Note: For normalized vectors, this equals cosine similarity.
  /// The result is negated for ORDER BY to work correctly (lower = more similar).
  /// </summary>
  InnerProduct = 1,

  /// <summary>
  /// Cosine distance. Lower values indicate more similar vectors.
  /// PostgreSQL operator: <![CDATA[<=>]]>
  /// Formula: 1 - cosine_similarity(a, b)
  /// Value range: 0 (identical) to 2 (opposite).
  /// </summary>
  Cosine = 2
}
