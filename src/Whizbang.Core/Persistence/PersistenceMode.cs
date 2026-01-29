namespace Whizbang.Core.Persistence;

/// <summary>
/// Specifies how events are persisted by receptors.
/// </summary>
/// <remarks>
/// <para>
/// Different receptors may need different persistence strategies based on their
/// requirements for throughput, consistency, and reliability.
/// </para>
/// <para>
/// Configure globally via <c>Persistence.DefaultMode</c> in appsettings.json,
/// or per-receptor via <see cref="Attributes.PersistenceStrategyAttribute"/>.
/// </para>
/// </remarks>
/// <docs>core-concepts/persistence#modes</docs>
public enum PersistenceMode {
  /// <summary>
  /// Events are committed immediately after each AppendAsync call.
  /// Default mode - no explicit SaveChanges needed.
  /// </summary>
  /// <remarks>
  /// Best for: Critical business operations requiring immediate consistency.
  /// Trade-off: Lower throughput due to per-event commits.
  /// </remarks>
  Immediate = 0,

  /// <summary>
  /// Events are buffered and committed on FlushAsync or when batch threshold is reached.
  /// Configure batch size and flush interval in appsettings.json.
  /// </summary>
  /// <remarks>
  /// Best for: High-throughput event ingestion scenarios.
  /// Trade-off: Events not visible until flush; potential data loss on crash.
  /// </remarks>
  Batched = 1,

  /// <summary>
  /// Events are queued for reliable delivery via IWorkCoordinator.
  /// Ensures at-least-once delivery with automatic retries.
  /// </summary>
  /// <remarks>
  /// Best for: Cross-service coordination, integration events.
  /// Trade-off: Higher latency due to outbox pattern overhead.
  /// </remarks>
  Outbox = 2
}
