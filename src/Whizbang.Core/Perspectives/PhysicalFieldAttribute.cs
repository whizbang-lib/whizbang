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
/// <docs>perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PhysicalFieldAttributeTests.cs</tests>
/// <example>
/// <code>
/// [PerspectiveStorage(FieldStorageMode.Extracted)]
/// public record ProductDto {
///   [StreamKey]
///   public Guid ProductId { get; init; }
///
///   [PhysicalField(Indexed = true)]
///   public Guid CategoryId { get; init; }
///
///   [PhysicalField(Indexed = true, MaxLength = 100)]
///   public string Sku { get; init; }
///
///   // Non-physical property stays in JSONB only
///   public string Description { get; init; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class PhysicalFieldAttribute : Attribute {
  /// <summary>
  /// Whether to create a database index on this column.
  /// Defaults to false. For composite indexes, use <see cref="CompositeIndexAttribute"/> on the model class.
  /// </summary>
  public bool Indexed { get; init; }

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
}
