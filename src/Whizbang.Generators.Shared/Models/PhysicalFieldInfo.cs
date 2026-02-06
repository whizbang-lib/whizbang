namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Value type containing information about a discovered physical field on a perspective model.
/// This record uses value equality which is critical for incremental generator performance.
/// Physical fields are marked with [PhysicalField] or [VectorField] attributes.
/// </summary>
/// <param name="PropertyName">Name of the property on the model</param>
/// <param name="ColumnName">Database column name (snake_case, or custom from attribute)</param>
/// <param name="TypeName">Fully qualified type name of the property</param>
/// <param name="IsIndexed">Whether an index should be created</param>
/// <param name="IsUnique">Whether a unique constraint should be applied</param>
/// <param name="MaxLength">Maximum length for string fields (VARCHAR constraint)</param>
/// <param name="IsVector">Whether this is a vector field (float[])</param>
/// <param name="VectorDimensions">Dimension count for vector fields</param>
/// <param name="VectorDistanceMetric">Distance metric for vector index (L2=0, InnerProduct=1, Cosine=2)</param>
/// <param name="VectorIndexType">Index type for vectors (None=0, IVFFlat=1, HNSW=2)</param>
/// <param name="VectorIndexLists">Number of lists for IVFFlat index</param>
/// <docs>perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Generators.Tests/Models/PhysicalFieldInfoTests.cs</tests>
public sealed record PhysicalFieldInfo(
    string PropertyName,
    string ColumnName,
    string TypeName,
    bool IsIndexed,
    bool IsUnique,
    int? MaxLength,
    bool IsVector,
    int? VectorDimensions,
    GeneratorVectorDistanceMetric? VectorDistanceMetric,
    GeneratorVectorIndexType? VectorIndexType,
    int? VectorIndexLists
);

/// <summary>
/// Distance metric for pgvector index operations.
/// Mirrors Whizbang.Core.Perspectives.VectorDistanceMetric for generator use.
/// </summary>
public enum GeneratorVectorDistanceMetric {
  /// <summary>L2 (Euclidean) distance - uses &lt;-&gt; operator</summary>
  L2 = 0,

  /// <summary>Inner product (negative) - uses &lt;#&gt; operator</summary>
  InnerProduct = 1,

  /// <summary>Cosine distance - uses &lt;=&gt; operator</summary>
  Cosine = 2
}

/// <summary>
/// Index type for pgvector columns.
/// Mirrors Whizbang.Core.Perspectives.VectorIndexType for generator use.
/// </summary>
public enum GeneratorVectorIndexType {
  /// <summary>No index - exact (sequential) search</summary>
  None = 0,

  /// <summary>IVFFlat - good balance of speed and accuracy</summary>
  IVFFlat = 1,

  /// <summary>HNSW - better recall, more memory</summary>
  HNSW = 2
}
