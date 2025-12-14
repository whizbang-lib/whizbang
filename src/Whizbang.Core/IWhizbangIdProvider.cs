namespace Whizbang.Core;

/// <summary>
/// Provides globally unique identifiers for WhizbangId types.
/// Implement this interface to customize ID generation strategy (e.g., UUIDv7, sequential, testing).
/// </summary>
/// <docs>core-concepts/message-context</docs>
public interface IWhizbangIdProvider {
  /// <summary>
  /// Generates a new globally unique identifier.
  /// Default implementation uses UUIDv7 for time-ordered, database-friendly IDs.
  /// </summary>
  /// <returns>A new Guid value.</returns>
  Guid NewGuid();
}
