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
/// <param name="ColumnType">
/// The author's own PostgreSQL type for the column, or null to derive one from the CLR type. Last in
/// the list and defaulted so the construction sites that do not set it are unaffected.
/// </param>
/// <param name="IsSplit">
/// True when the model stores this field in the column only (Split storage), so the document has no copy
/// of it to backfill a new column from. Last and defaulted for the same reason as <c>ColumnType</c>.
/// </param>
/// <param name="IsSearch">
/// True when a text field declares <c>IndexKinds.Search</c>: its column gets a trigram index over the
/// framework's fold, and a <c>Contains</c> on it is folded to match.
/// </param>
/// <param name="EnumScalarType">
/// For an enumeration, the fully qualified CLR name of the scalar its column holds (see
/// <see cref="PhysicalFieldScalar"/>); null otherwise. The column is typed from it and EF Core converts to it.
/// </param>
/// <param name="EnumMembers">
/// For an enumeration, its members as <c>Name=Value;…</c> (see <see cref="PhysicalFieldScalar.EnumMembers"/>), from
/// which the rewrite converting a text column of names to numbers is generated; null otherwise.
/// </param>
/// <param name="EnumIsFlags">
/// For an enumeration marked <c>[Flags]</c>, true: its stored names may be combined (<c>"A, B"</c>), and the rewrite
/// converts a combination to the bitwise OR of the members' values.
/// </param>
/// <param name="IsInitOnly">
/// True when the property's setter is <c>init</c>: code generated to copy a column into a model that already
/// exists sets it through a <c>with</c> expression on a record, and cannot set it on a class (issue #982).
/// </param>
/// <param name="IsReadOnly">
/// True when the property has no setter at all (a computed value), so there is nothing to copy a column into.
/// </param>
/// <param name="Storage">The column's declared <c>SET STORAGE</c> strategy (<c>MAIN</c>, …), or null to leave it.</param>
/// <param name="Compression">The column's declared compression method (<c>lz4</c>, <c>pglz</c>), or null to leave it.</param>
/// <param name="MaxBytes">The column's declared size budget in bytes, enforced by a check constraint, or null.</param>
/// <param name="IsContainmentIndexed">
/// True when a jsonb column declares <c>[Indexed(IndexKinds.Containment)]</c>: it gets a GIN <c>jsonb_path_ops</c> index.
/// </param>
/// <docs>fundamentals/perspectives/physical-fields</docs>
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
    int? VectorIndexLists,
    string? ColumnType = null,
    bool IsSplit = false,
    bool IsSearch = false,
    string? EnumScalarType = null,
    string? EnumMembers = null,
    bool EnumIsFlags = false,
    bool IsInitOnly = false,
    bool IsReadOnly = false,
    string? Storage = null,
    string? Compression = null,
    int? MaxBytes = null,
    bool IsContainmentIndexed = false
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
