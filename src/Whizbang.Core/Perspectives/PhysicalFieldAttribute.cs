namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marks a property to be stored as a physical database column in addition to or instead of JSONB.
/// Physical columns enable native database indexing, type constraints, and optimized queries.
/// The storage behavior depends on the model's <see cref="PerspectiveStorageAttribute"/> setting.
/// </summary>
/// <remarks>
/// <para>
/// Use this attribute on properties that are frequently queried or filtered.
/// The source generator will create a dedicated database column for each marked property.
/// </para>
/// <para>
/// <strong>Storage Modes:</strong>
/// </para>
/// <list type="bullet">
/// <item><see cref="FieldStorageMode.Extracted"/>: Property exists in both JSONB and physical column (indexed copy)</item>
/// <item><see cref="FieldStorageMode.Split"/>: Property exists only in physical column, excluded from JSONB</item>
/// </list>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PhysicalFieldAttributeTests.cs</tests>
/// <example>
/// <code>
/// [PerspectiveStorage(FieldStorageMode.Extracted)]
/// public record ProductDto {
///   [StreamId]
///   public Guid ProductId { get; init; }
///
///   // Promoted to a column, and indexed: [Indexed] is how any field asks for an index.
///   [PhysicalField]
///   [Indexed]
///   public Guid CategoryId { get; init; }
///
///   [PhysicalField(MaxLength = 100)]
///   public string Sku { get; init; }
///
///   // Non-physical property stays in JSONB only
///   public string Description { get; init; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class PhysicalFieldAttribute : Attribute {
  // This attribute promotes a property to a real column. It deliberately does not decide whether the
  // column is indexed: [Indexed] does that, for a promoted field and a document field alike, so an
  // author asking for an index writes the same thing either way. A column is what you need for a
  // constraint, a foreign key or uniqueness, and those stay here because they are properties of the
  // column rather than requests for an index.

  /// <summary>
  /// Whether this column should have a UNIQUE constraint.
  /// Defaults to false.
  /// </summary>
  public bool Unique { get; init; }

  /// <summary>
  /// Optional custom column name. If not specified, defaults to snake_case of property name.
  /// </summary>
  /// <example>
  /// [PhysicalField(ColumnName = "ext_id")]
  /// public string ExternalId { get; init; }
  /// // Creates column: ext_id instead of external_id
  /// </example>
  public string? ColumnName { get; init; }

  /// <summary>
  /// Maximum length for string columns. -1 or 0 means unlimited (TEXT type in PostgreSQL).
  /// Only applicable to string properties.
  /// </summary>
  /// <remarks>
  /// Set to a positive integer to create a VARCHAR(N) column.
  /// Leave at default (-1) or set to 0 for unlimited TEXT type.
  /// </remarks>
  public int MaxLength { get; init; } = -1;

  /// <summary>
  /// The PostgreSQL type for this column, overriding the one derived from the property's CLR type.
  /// Null keeps the derived type, which is the default.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The conventional mapping covers the types a model usually holds. This is for the ones it
  /// cannot reach: a native array, so <c>Guid[]</c> lands in <c>uuid[]</c> where containment and
  /// overlap operators are available on it rather than in a delimited string the application has to
  /// encode and decode forever; a narrower or wider type than the convention picks; and a domain or
  /// extension type the mapper does not know about.
  /// </para>
  /// <para>
  /// Written through verbatim, so it is the author's responsibility that the type exists and that
  /// the property's stored form fits it. Nothing here validates it, because the set of types a
  /// server might have is open -- a domain, an enum or a type from an extension are all legitimate,
  /// and a check against a fixed list would refuse exactly the cases this exists for. A type the
  /// server does not have fails the schema pass with the server's own message, which says more than
  /// a guess made at build time would.
  /// </para>
  /// <para>
  /// An array column is not much use without an index method that suits it, which is a separate
  /// declaration.
  /// </para>
  /// </remarks>
  /// <example>
  /// [PhysicalField(ColumnType = "uuid[]")]
  /// public Guid[] AncestorIds { get; init; } = [];
  /// </example>
  public string? ColumnType { get; init; }
}
