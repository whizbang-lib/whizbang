namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Specifies how to look up pending events for synchronization.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Local Mode:</strong> Uses in-memory tracking within the current request/scope.
/// Fast but only knows about events emitted in this instance.
/// </para>
/// <para>
/// <strong>Distributed Mode:</strong> Queries the database for pending events.
/// Works across multiple instances but has higher latency.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public enum SyncLookupMode {
  /// <summary>
  /// Check in-memory tracker only (fast, single instance).
  /// </summary>
  /// <remarks>
  /// Best for same-request consistency and single-instance deployments.
  /// </remarks>
  Local,

  /// <summary>
  /// Query database for pending events (slower, multi-instance).
  /// </summary>
  /// <remarks>
  /// Best for multi-instance deployments and cross-request consistency.
  /// </remarks>
  Distributed
}
