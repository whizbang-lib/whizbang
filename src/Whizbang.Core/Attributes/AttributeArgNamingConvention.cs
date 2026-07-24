namespace Whizbang.Core.Attributes;

/// <summary>
/// Names the convention by which a tag-attribute's positional constructor parameter names
/// map to the corresponding init-only properties on the attribute. The
/// <c>MessageTagDiscoveryGenerator</c> uses the selected convention when emitting an
/// <c>AttributeFactory</c>'s object initializer so reconstructed attribute instances
/// preserve all the values from the original declaration — including positional ctor args
/// (e.g., <c>tagValue</c>, <c>propertyName</c>) that would otherwise be lost when the
/// generator falls back to <c>new()</c> + named-only initializers.
/// </summary>
/// <remarks>
/// Default for tag attributes that don't declare an <see cref="AttributeArgNamingAttribute"/>
/// is <see cref="PascalCase"/>, since that's the prevailing C# convention (parameter
/// <c>tagValue</c> initializes property <c>TagValue</c>).
/// </remarks>
public enum AttributeArgNamingConvention {
  /// <summary>
  /// Capitalize the first character: <c>tagValue</c> → <c>TagValue</c>. Most common in C#
  /// (parameter follows camelCase, property follows PascalCase).
  /// </summary>
  PascalCase = 0,

  /// <summary>
  /// Leave the parameter name unchanged: <c>TagValue</c> → <c>TagValue</c>. Use when the
  /// constructor parameter is already PascalCase or matches the property name verbatim.
  /// </summary>
  Identity = 1,

  /// <summary>
  /// Lowercase the first character: <c>TagValue</c> → <c>tagValue</c>. Use when the
  /// property follows camelCase (rare for C# but useful for JSON-style records).
  /// </summary>
  CamelCase = 2,

  /// <summary>
  /// Insert underscores at uppercase boundaries and lowercase: <c>tagValue</c> →
  /// <c>tag_value</c>. Sequences of uppercase letters become a single token
  /// (<c>HTTPRequest</c> → <c>http_request</c>).
  /// </summary>
  SnakeCase = 3,

  /// <summary>
  /// Like <see cref="SnakeCase"/> but with hyphens: <c>tagValue</c> → <c>tag-value</c>.
  /// </summary>
  KebabCase = 4,

  /// <summary>
  /// Uppercase variant of <see cref="SnakeCase"/>: <c>tagValue</c> → <c>TAG_VALUE</c>.
  /// </summary>
  UpperSnake = 5,
}
