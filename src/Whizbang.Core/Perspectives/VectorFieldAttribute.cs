namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marks a float[] property as a vector column for similarity search using pgvector.
/// Enables efficient nearest-neighbor queries using various distance metrics.
/// </summary>
/// <remarks>
/// <para>
/// Requires the pgvector extension in PostgreSQL. The property must be <c>float[]</c> or <c>float[]?</c>.
/// Vector fields are always stored as physical columns (implicit <see cref="PhysicalFieldAttribute"/>).
/// </para>
/// <para>
/// For optimal performance with large datasets, enable indexing with either IVFFlat or HNSW.
/// HNSW provides better recall but uses more memory; IVFFlat is faster to build.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/vector-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/VectorFieldAttributeTests.cs</tests>
/// <example>
/// <code>
/// [PerspectiveStorage(FieldStorageMode.Split)]
/// public record ProductSearchDto {
///   [StreamId]
///   public Guid ProductId { get; init; }
///
///   // OpenAI embeddings (1536 dimensions)
///   [VectorField(1536)]
///   [Indexed]
///   public float[]? ContentEmbedding { get; init; }
///
///   // With custom settings
///   [VectorField(768, DistanceMetric = VectorDistanceMetric.Cosine, IndexType = VectorIndexType.HNSW)]
///   [Indexed]
///   public float[]? TitleEmbedding { get; init; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class VectorFieldAttribute(int dimensions) : Attribute {
  /// <summary>
  /// The number of dimensions in the vector (e.g., 1536 for OpenAI text-embedding-ada-002).
  /// Must be a positive integer.
  /// </summary>
  public int Dimensions { get; } = dimensions >= 1 ? dimensions : throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be at least 1");

  /// <summary>
  /// The distance metric for similarity queries. Defaults to <see cref="VectorDistanceMetric.Cosine"/>.
  /// </summary>
  public VectorDistanceMetric DistanceMetric { get; init; } = VectorDistanceMetric.Cosine;

  /// <summary>
  /// The index algorithm to use when the field is marked <c>[Indexed]</c>.
  /// Defaults to <see cref="VectorIndexType.IVFFlat"/>.
  /// </summary>
  public VectorIndexType IndexType { get; init; } = VectorIndexType.IVFFlat;

  /// <summary>
  /// Number of lists for IVFFlat index. Higher values = faster queries, more memory.
  /// Defaults to 100. Only applicable when <see cref="IndexType"/> is <see cref="VectorIndexType.IVFFlat"/>.
  /// Recommended: sqrt(number of rows) for small datasets, number of rows / 1000 for large datasets.
  /// </summary>
  public int IndexLists { get; init; } = 100;

  /// <summary>
  /// Optional custom column name. Defaults to snake_case of property name.
  /// </summary>
  public string? ColumnName { get; init; }
}
