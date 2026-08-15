namespace Whizbang.Core.Attributes;

/// <summary>
/// Bounds how MANY rows a perspective retains per scope, keeping the most recently active and
/// evicting the rest.
/// </summary>
/// <remarks>
/// <para>
/// Companion to <see cref="RowTtlAttribute"/>, not an alternative. A time window bounds AGE but
/// never CARDINALITY — a heavy scope can hold thousands of rows all created inside the window.
/// Redis streams pair <c>MAXLEN</c> with age trimming, EventStoreDB pairs <c>$maxCount</c> with
/// <c>$maxAge</c>, Kafka pairs <c>retention.bytes</c> with <c>retention.ms</c>, for this reason.
/// </para>
/// <para>
/// On a Sourced perspective eviction is NOT lossy: resurrection-on-wake re-folds an evicted row from
/// the log the moment its stream is touched, and the revived row ranks first by business time. A
/// capped Sourced perspective is therefore an LRU cache over the event log rather than a truncation,
/// which makes aggressive caps safe in a way they are not in the systems this pattern comes from.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RowCapAttribute : Attribute {
  /// <summary>Maximum rows kept per (tenant, user) scope. -1 leaves cardinality unbounded.</summary>
  public int PerScope { get; init; } = -1;

  /// <summary>Maximum rows kept per tenant. -1 leaves cardinality unbounded.</summary>
  public int PerTenant { get; init; } = -1;
}
