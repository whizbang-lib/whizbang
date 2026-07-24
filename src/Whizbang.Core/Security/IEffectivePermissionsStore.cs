namespace Whizbang.Core.Security;

/// <summary>
/// Per-user materialized "effective permissions" cache: the flattened, read-optimized
/// shape that authorization decisions key off of, rebuilt on writes so reads stay cheap.
/// Generic over the user-identity type so consumers can use Guid, string, or a domain
/// value object as the key.
/// </summary>
/// <remarks>
/// <para>
/// Pattern: a user's effective permissions are derived from many sources — direct grants,
/// roles/personas, group memberships, organizational hierarchies, scope overrides. Computing
/// them on every authorization check is wasteful; materializing them once after each
/// state change and reading the cache thereafter is much faster.
/// </para>
/// <para>
/// Implementations decide what the snapshot contains (permission strings, group ids,
/// custom claims, hash for change detection) — see <see cref="EffectivePermissionsSnapshot"/>
/// for the canonical shape.
/// </para>
/// <para>
/// a consumer application currently implements this pattern bespoke for its persona-security work:
/// <c>UserEffectivePermissionsModel</c> + <c>MaterializeUserEffectivePermissionsCommand</c>
/// + cascade receptors. The generic abstraction allows other applications to drop into
/// the same pattern without re-deriving it.
/// </para>
/// </remarks>
/// <typeparam name="TUserId">
/// User identity type. Typically <see cref="System.Guid"/> or <see cref="string"/>; can be
/// any type with sensible equality semantics.
/// </typeparam>
/// <docs>fundamentals/security/effective-permissions</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/IEffectivePermissionsStoreTests.cs</tests>
public interface IEffectivePermissionsStore<TUserId> {
  /// <summary>
  /// Reads the user's current materialized effective permissions snapshot, or <c>null</c>
  /// if no snapshot has been materialized yet (brand-new user).
  /// </summary>
  ValueTask<EffectivePermissionsSnapshot?> GetAsync(TUserId userId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Persists a new materialized snapshot for the user, replacing any existing one.
  /// Idempotent — implementations should compare the supplied snapshot's hash to the
  /// existing one and skip the write when they match.
  /// </summary>
  ValueTask SetAsync(TUserId userId, EffectivePermissionsSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical shape of a materialized effective-permissions snapshot. Stores flat
/// permission keys, group ids, role names, and the source content hash so consumers can
/// detect "no change" without comparing every field.
/// </summary>
/// <param name="Permissions">
/// Flat permission strings (e.g., <c>"orders:read"</c>, <c>"nav.job"</c>). Sorted and
/// deduplicated by implementations for stable hashing.
/// </param>
/// <param name="GroupIds">
/// Security groups the user belongs to. Stored as strings so consumers can use any id
/// shape (Guid, string, etc).
/// </param>
/// <param name="RoleNames">
/// Role/persona names assigned to the user. Display-friendly; not used for authorization
/// decisions (which key off Permissions).
/// </param>
/// <param name="Hash">
/// Content hash over the snapshot. Implementations decide the algorithm (SHA-256 over a
/// canonical string is typical). Used for idempotent <c>SetAsync</c>.
/// </param>
/// <param name="Version">
/// Monotonically increasing version per user, incremented on every emit. Useful for
/// optimistic-concurrency or "bring me anything newer than X" diagnostics.
/// </param>
/// <param name="ComputedAt">When the snapshot was computed.</param>
public sealed record EffectivePermissionsSnapshot(
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> GroupIds,
    IReadOnlyList<string> RoleNames,
    string Hash,
    long Version,
    DateTimeOffset ComputedAt);
