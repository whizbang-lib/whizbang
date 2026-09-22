using Whizbang.Core.ValueObjects;

namespace Whizbang.Core;

/// <summary>
/// Provides globally unique identifiers for WhizbangId types.
/// Implement this interface to customize ID generation strategy (e.g., UUIDv7, sequential, testing).
/// </summary>
/// <docs>fundamentals/messages/message-context</docs>
public interface IWhizbangIdProvider {
  /// <summary>
  /// Generates a new globally unique identifier with tracking metadata.
  /// Default implementation uses UUIDv7 for time-ordered, database-friendly IDs.
  /// </summary>
  /// <returns>A TrackedGuid with creation metadata for version and source tracking.</returns>
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdProviderTests.cs:NewGuid_WithDefaultProvider_ShouldReturnUuidV7Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdProviderTests.cs:NewGuid_WithCustomProvider_ShouldReturnCustomTrackedGuidAsync</tests>
  TrackedGuid NewGuid();
}
