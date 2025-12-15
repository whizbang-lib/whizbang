namespace Whizbang.Generators;

/// <summary>
/// Discovery source for a WhizbangId.
/// Indicates how the ID type was discovered during source generation.
/// </summary>
/// <tests>No tests found</tests>
internal enum DiscoverySource {
  /// <summary>
  /// Discovered via explicit type declaration with [WhizbangId] attribute.
  /// Example: [WhizbangId] public readonly partial struct ProductId;
  /// </summary>
  /// <tests>No tests found</tests>
  ExplicitType,

  /// <summary>
  /// Discovered via property with [WhizbangId] attribute.
  /// Example: public class Foo { [WhizbangId] public ProductId Id { get; set; } }
  /// </summary>
  /// <tests>No tests found</tests>
  Property,

  /// <summary>
  /// Discovered via primary constructor parameter with [WhizbangId] attribute.
  /// Example: public record Msg([WhizbangId] ProductId Id);
  /// </summary>
  /// <tests>No tests found</tests>
  Parameter
}

/// <summary>
/// Value type containing information about a discovered WhizbangId.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="TypeName">Simple type name without namespace (e.g., "ProductId")</param>
/// <param name="Namespace">Namespace where the ID type should be generated (e.g., "MyApp.Domain")</param>
/// <param name="Source">How the ID was discovered (ExplicitType, Property, or Parameter)</param>
/// <param name="SuppressDuplicateWarning">True if WHIZ024 warning should be suppressed for this ID</param>
/// <tests>No tests found</tests>
internal sealed record WhizbangIdInfo(
  string TypeName,
  string Namespace,
  DiscoverySource Source,
  bool SuppressDuplicateWarning = false
) {
  /// <summary>
  /// Gets the fully qualified name of the ID type (Namespace.TypeName).
  /// Used for deduplication and collision detection.
  /// </summary>
  /// <tests>No tests found</tests>
  public string FullyQualifiedName => $"{Namespace}.{TypeName}";

  /// <summary>
  /// Value equality is used for deduplication.
  /// Two WhizbangIdInfo instances are equal if they have the same FullyQualifiedName.
  /// </summary>
  /// <tests>No tests found</tests>
  public bool Equals(WhizbangIdInfo? other) =>
    other is not null && FullyQualifiedName == other.FullyQualifiedName;

  /// <summary>
  /// Hash code based on FullyQualifiedName for deduplication.
  /// </summary>
  /// <tests>No tests found</tests>
  public override int GetHashCode() => FullyQualifiedName.GetHashCode();
}
