namespace Whizbang.Core.Tests.Helpers;

/// <summary>
/// Helper methods for testing attribute metadata.
/// </summary>
internal static class AttributeTestHelpers {
  /// <summary>
  /// Gets the AttributeUsageAttribute for a given attribute type.
  /// </summary>
  public static AttributeUsageAttribute? GetAttributeUsage<TAttribute>() where TAttribute : Attribute {
    return typeof(TAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();
  }

  /// <summary>
  /// Gets the AttributeUsageAttribute for a given attribute type.
  /// </summary>
  public static AttributeUsageAttribute? GetAttributeUsage(Type attributeType) {
    return attributeType
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();
  }
}
