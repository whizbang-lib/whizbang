namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares a perspective model's membership in a stream deletion group. Perspectives sharing a
/// group key (within one service) evict coherently: when a stream's row is evicted from one member
/// by its own retention (row TTL, cap), the same stream's rows leave the other members in the same
/// maintenance cycle — no more half-dead streams whose satellite rows linger on independent clocks.
/// </summary>
/// <remarks>
/// <para>
/// Repeatable — a perspective may belong to several groups, and each MEMBERSHIP carries its own
/// dials. The load-bearing distinction is <b>own-origin vs received</b>: a perspective's own
/// evictions are announced to every group whose membership has <see cref="Announce"/> on (that is
/// not bridging); whether an eviction RECEIVED through one group re-announces into another is
/// <see cref="Bridge"/>, off by default so two groups sharing a member don't silently weld into
/// one transitive deletion graph.
/// </para>
/// <para>
/// The group controls <i>togetherness</i>, not <i>whether</i>: something must still evict first,
/// so at least one member needs a real evictor (<see cref="RowTtlAttribute"/> /
/// <see cref="RowCapAttribute"/>), and every member should keep its own backstop TTL. A
/// perspective with no membership is untouchable by cascades regardless of what streams it shares.
/// The event store is never touched — an evicted stream re-folds everywhere on its next event.
/// </para>
/// </remarks>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveStreamGroupRegistryTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class StreamGroupAttribute(string key) : Attribute {
  /// <summary>The group key. Same key within a service = one group.</summary>
  public string Key { get; } = key;

  /// <summary>
  /// Whether this perspective's OWN-ORIGIN evictions (its row TTL, its cap, its explicit purge)
  /// are announced to this group. Default true.
  /// </summary>
  public bool Announce { get; set; } = true;

  /// <summary>Whether this perspective's row dies when this group announces a stream. Default true.</summary>
  public bool Follow { get; set; } = true;

  /// <summary>
  /// Whether an eviction RECEIVED through another group re-announces into this one. Default
  /// FALSE — bridging is the explicit opt-in that welds two groups into a transitive graph.
  /// </summary>
  public bool Bridge { get; set; }
}
