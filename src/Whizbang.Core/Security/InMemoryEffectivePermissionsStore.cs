using System.Collections.Concurrent;

namespace Whizbang.Core.Security;

/// <summary>
/// Trivial in-memory <see cref="IEffectivePermissionsStore{TUserId}"/> implementation
/// backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>. Useful for tests, local
/// development, single-process apps, and as a reference implementation for production
/// stores backed by Redis / database / event-sourced perspectives.
/// </summary>
/// <typeparam name="TUserId">User identity type with sensible equality semantics.</typeparam>
/// <docs>fundamentals/security/effective-permissions#in-memory</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/InMemoryEffectivePermissionsStoreTests.cs</tests>
public sealed class InMemoryEffectivePermissionsStore<TUserId> : IEffectivePermissionsStore<TUserId>
    where TUserId : notnull {
  private readonly ConcurrentDictionary<TUserId, EffectivePermissionsSnapshot> _store = new();

  /// <inheritdoc />
  public ValueTask<EffectivePermissionsSnapshot?> GetAsync(TUserId userId, CancellationToken cancellationToken = default) {
    return new ValueTask<EffectivePermissionsSnapshot?>(_store.TryGetValue(userId, out var snapshot) ? snapshot : null);
  }

  /// <inheritdoc />
  public ValueTask SetAsync(TUserId userId, EffectivePermissionsSnapshot snapshot, CancellationToken cancellationToken = default) {
    // Idempotent: skip when the supplied snapshot's hash matches the existing entry.
    if (_store.TryGetValue(userId, out var existing) && existing.Hash == snapshot.Hash) {
      return default;
    }
    _store[userId] = snapshot;
    return default;
  }
}
