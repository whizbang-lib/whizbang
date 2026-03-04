namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered array type used in messages.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="ArrayTypeName">Fully qualified array type name (e.g., "global::Whizbang.Core.IEvent[]")</param>
/// <param name="ElementTypeName">Fully qualified element type name (e.g., "global::Whizbang.Core.IEvent")</param>
/// <param name="ElementSimpleName">Simple element type name for method generation (e.g., "IEvent")</param>
/// <docs>internals/json-serialization-customizations</docs>
public sealed record ArrayTypeInfo(
    string ArrayTypeName,
    string ElementTypeName,
    string ElementSimpleName
) {
  /// <summary>
  /// Unique identifier derived from element type name, suitable for C# identifiers.
  /// Strips "global::" prefix and replaces special characters with "_".
  /// E.g., "global::Whizbang.Core.IEvent" becomes "Whizbang_Core_IEvent".
  /// E.g., "global::System.Collections.Generic.Dictionary&lt;string, string&gt;" becomes
  /// "System_Collections_Generic_Dictionary_string__string_".
  /// This prevents duplicate field/method names when element types have the same SimpleName.
  /// </summary>
  public string ElementUniqueIdentifier => ElementTypeName
    .Replace("global::", "")
    .Replace(".", "_")
    .Replace("<", "_")
    .Replace(">", "_")
    .Replace(",", "_")
    .Replace(" ", "")
    .Replace("?", "__Nullable");
}
