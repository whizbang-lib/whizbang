namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marks a property as a polymorphic discriminator. The source generator will create
/// a physical indexed database column for efficient querying of polymorphic types.
/// </summary>
/// <remarks>
/// <para>
/// Use this attribute on properties that store the type discriminator for polymorphic
/// JSON data (e.g., abstract base classes with derived types). The discriminator column
/// enables efficient SQL queries without parsing JSON at query time.
/// </para>
/// <para>
/// The discriminator value is typically the fully-qualified type name of the derived type,
/// enabling type-safe queries via the <c>WherePolymorphic</c> extension methods.
/// </para>
/// </remarks>
/// <docs>perspectives/polymorphic-discriminator</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PolymorphicDiscriminatorAttributeTests.cs</tests>
/// <example>
/// <code>
/// public record Field {
///   public Guid Id { get; init; }
///
///   [PolymorphicDiscriminator(ColumnName = "settings_type")]
///   public string SettingsTypeName { get; init; }
///
///   public AbstractFieldSettings FieldSettings { get; init; }
/// }
///
/// // Query using the discriminator column (full SQL, indexed):
/// var results = await query
///   .Where(r => r.Data.Fields.Any(f => f.SettingsTypeName == "TextFieldSettings"))
///   .ToListAsync();
///
/// // Or use the type-safe polymorphic API:
/// var results = await query
///   .WherePolymorphic(m => m.Fields)
///   .As&lt;TextFieldSettings&gt;(f => f.MaxLength > 100)
///   .ToListAsync();
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class PolymorphicDiscriminatorAttribute : Attribute {
  /// <summary>
  /// Optional custom column name for the discriminator. If not specified, defaults to
  /// snake_case of the property name.
  /// </summary>
  /// <example>
  /// [PolymorphicDiscriminator(ColumnName = "type_discriminator")]
  /// public string TypeName { get; init; }
  /// // Creates column: type_discriminator instead of type_name
  /// </example>
  public string? ColumnName { get; init; }
}
