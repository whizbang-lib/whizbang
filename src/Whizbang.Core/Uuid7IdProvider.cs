using Medo;

namespace Whizbang.Core;

/// <summary>
/// Default WhizbangId provider that generates time-ordered UUIDv7 identifiers.
/// UUIDv7 provides:
/// - Time-based ordering (sortable by creation time)
/// - Database index efficiency (sequential clustering)
/// - Standard GUID compatibility
/// </summary>
public sealed class Uuid7IdProvider : IWhizbangIdProvider {
  /// <summary>
  /// Generates a new time-ordered UUIDv7.
  /// </summary>
  /// <returns>A new Guid value using UUIDv7 format.</returns>
  public Guid NewGuid() => Uuid7.NewUuid7().ToGuid();
}
