namespace Whizbang.Core.Perspectives;

/// <summary>
/// Configures how physical fields are stored relative to JSONB for a perspective model.
/// Applied to the model class (not the perspective class).
/// </summary>
/// <remarks>
/// <para>
/// This attribute controls the storage strategy for properties marked with
/// <see cref="PhysicalFieldAttribute"/> or <see cref="VectorFieldAttribute"/>.
/// </para>
/// <para>
/// <strong>Storage Modes:</strong>
/// </para>
/// <list type="bullet">
/// <item><see cref="FieldStorageMode.JsonOnly"/>: No physical columns; all data in JSONB (default, backwards compatible)</item>
/// <item><see cref="FieldStorageMode.Extracted"/>: JSONB contains full model; physical columns are indexed copies</item>
/// <item><see cref="FieldStorageMode.Split"/>: Physical columns contain marked fields; JSONB contains remainder only</item>
/// </list>
/// <para>
/// If this attribute is not present on a model, it defaults to <see cref="FieldStorageMode.JsonOnly"/>
/// for backwards compatibility.
/// </para>
/// </remarks>
/// <docs>perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveStorageAttributeTests.cs</tests>
/// <example>
/// <code>
/// // Extracted mode: JSONB contains full model, physical columns are indexed copies
/// [PerspectiveStorage(FieldStorageMode.Extracted)]
/// public record ProductDto {
///   [PhysicalField(Indexed = true)]
///   public decimal Price { get; init; }
///   public string Description { get; init; }
/// }
///
/// // Split mode: Physical columns contain marked fields, JSONB contains remainder only
/// [PerspectiveStorage(FieldStorageMode.Split)]
/// public record ProductSearchDto {
///   [VectorField(1536)]
///   public float[]? Embedding { get; init; }  // Only in physical column
///   public string Name { get; init; }          // Only in JSONB
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class PerspectiveStorageAttribute : Attribute {
  /// <summary>
  /// The storage mode for physical fields in this model.
  /// </summary>
  public FieldStorageMode Mode { get; }

  /// <summary>
  /// Creates a perspective storage attribute with the specified mode.
  /// </summary>
  /// <param name="mode">The storage mode for physical fields.</param>
  public PerspectiveStorageAttribute(FieldStorageMode mode) {
    Mode = mode;
  }
}
