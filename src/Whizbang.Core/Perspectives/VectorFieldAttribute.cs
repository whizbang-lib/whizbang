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
/// <docs>perspectives/vector-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/VectorFieldAttributeTests.cs</tests>
/// <example>
/// <code>
/// [PerspectiveStorage(FieldStorageMode.Split)]
/// public record ProductSearchDto {
///   [StreamKey]
///   public Guid ProductId { get; init; }
///
///   // OpenAI embeddings (1536 dimensions)
///   [VectorField(1536)]
///   public float[]? ContentEmbedding { get; init; }
///
///   // With custom settings
///   [VectorField(768, DistanceMetric = VectorDistanceMetric.Cosine, IndexType = VectorIndexType.HNSW)]
///   public float[]? TitleEmbedding { get; init; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class VectorFieldAttribute : Attribute {
  /// <summary>
  /// The number of dimensions in the vector (e.g., 1536 for OpenAI text-embedding-ada-002).
  /// Must be a positive integer.
  /// </summary>
  public int Dimensions { get; }

  /// <summary>
  /// The distance metric for similarity queries. Defaults to <see cref="VectorDistanceMetric.Cosine"/>.
  /// </summary>
  public VectorDistanceMetric DistanceMetric { get; init; } = VectorDistanceMetric.Cosine;

  /// <summary>
  /// Whether to create an index on this vector column for efficient similarity search.
  /// Defaults to true. Uses <see cref="IndexType"/> to determine the index algorithm.
  /// </summary>
  public bool Indexed { get; init; } = true;

  /// <summary>
  /// The index type to use when <see cref="Indexed"/> is true.
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

  /// <summary>
  /// Creates a vector field attribute with the specified dimensions.
  /// </summary>
  /// <param name="dimensions">Number of dimensions (e.g., 1536 for OpenAI embeddings, 768 for sentence-transformers).</param>
  /// <exception cref="ArgumentOutOfRangeException">Thrown when dimensions is less than 1.</exception>
  public VectorFieldAttribute(int dimensions) {
    ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
    Dimensions = dimensions;
  }
}
