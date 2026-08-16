using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>One perspective's membership in a stream deletion group, with its dials.</summary>
/// <param name="Key">The group key (service-local).</param>
/// <param name="Announce">Own-origin evictions are announced to this group.</param>
/// <param name="Follow">Group announcements evict this perspective's row for the stream.</param>
/// <param name="Bridge">Evictions received via another group re-announce into this one.</param>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveStreamGroupRegistryTests.cs</tests>
public sealed record StreamGroupMembership(string Key, bool Announce, bool Follow, bool Bridge);

/// <summary>
/// Registry mapping perspective model types to their <c>[StreamGroup]</c> memberships. Populated
/// by generated module initializers (one <c>Register</c> call per membership), read by the
/// maintenance cascade to compute the eviction closure. AOT-safe: types arrive as
/// <c>typeof(...)</c> from generated code, never reflection.
/// </summary>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveStreamGroupRegistryTests.cs</tests>
public static class PerspectiveStreamGroupRegistry {
  private static readonly ConcurrentDictionary<Type, List<StreamGroupMembership>> _memberships = new();

  /// <summary>Registers one membership for a model type. Idempotent per (type, key): last write wins.</summary>
  /// <param name="modelType">The perspective model type.</param>
  /// <param name="key">The group key.</param>
  /// <param name="announce">Own-origin evictions announce to this group.</param>
  /// <param name="follow">Group announcements evict this perspective's row.</param>
  /// <param name="bridge">Received evictions re-announce into this group.</param>
  public static void Register(Type modelType, string key, bool announce, bool follow, bool bridge) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentException.ThrowIfNullOrEmpty(key);
    _memberships.AddOrUpdate(
      modelType,
      _ => [new StreamGroupMembership(key, announce, follow, bridge)],
      (_, list) => {
        lock (list) {
          list.RemoveAll(m => m.Key == key);
          list.Add(new StreamGroupMembership(key, announce, follow, bridge));
          return list;
        }
      });
  }

  /// <summary>The memberships declared for a model type; empty when it joined no group.</summary>
  /// <param name="modelType">The perspective model type.</param>
  public static IReadOnlyList<StreamGroupMembership> Resolve(Type modelType) {
    ArgumentNullException.ThrowIfNull(modelType);
    if (!_memberships.TryGetValue(modelType, out var list)) {
      return [];
    }
    lock (list) {
      return [.. list];
    }
  }

  /// <summary>Every model type with at least one membership.</summary>
  public static IReadOnlyList<Type> RegisteredModels() => [.. _memberships.Keys];

  /// <summary>Test seam: clears all registrations.</summary>
  internal static void Clear() => _memberships.Clear();
}
