namespace Whizbang.Core.Perspectives;

/// <summary>
/// Declares how the stored form of a perspective model property changed, so the documents already stored are
/// converted in place at startup. Put it on the property as it is now, and say what it was.
/// </summary>
/// <remarks>
/// <para>
/// The generator turns each setting into an idempotent rewrite that converts only the values still in the old form
/// and leaves every other value alone. The rewrites run in the stored-format rewrite phase: once per schema, under
/// the schema lock, before the indexes are built. Each is journaled under a name that describes it, so a later
/// start skips it once a pass has found nothing left to convert. A value that cannot be converted stops startup
/// with the table, the path and a sample of the values.
/// </para>
/// <para>
/// Settings may be combined. A rename runs before a type conversion, and a type conversion before a default.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/StoredFormAttributeTests.cs</tests>
/// <example>
/// <code>
/// public record OrderModel {
///   // Was: public int Status { get; init; }
///   [StoredForm(Previously = typeof(int))]
///   public string Status { get; init; } = string.Empty;
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class StoredFormAttribute : Attribute {
  /// <summary>
  /// The property's former type. Stored values still in that type's form are converted to the current type's
  /// form: a number to a string, a numeric string to a number, an enum name to its number, and so on.
  /// </summary>
  public Type? Previously { get; set; }

  /// <summary>
  /// The property's former name. A stored value under the old key moves to the current key.
  /// </summary>
  public string? PreviousName { get; set; }

  /// <summary>
  /// The value written into stored documents that have no key for this property at all. A string is written as a
  /// JSON string, a number as a number, an enum as its number and a <see langword="bool"/> as a boolean.
  /// </summary>
  public object? DefaultWhenMissing { get; set; }
}
