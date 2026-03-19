namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Configuration options for perspective synchronization.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// var options = SyncFilter.ForStream(orderId)
///     .AndEventTypes&lt;OrderCreatedEvent&gt;()
///     .WithTimeout(TimeSpan.FromSeconds(10))
///     .Build();
/// </code>
/// <para>
/// All synchronization uses database-based lookup. The database is the only
/// authority for determining when perspectives have processed events.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public sealed class PerspectiveSyncOptions {
  /// <summary>
  /// Gets or sets the filter tree (supports AND/OR combinations).
  /// </summary>
  public required SyncFilterNode Filter { get; init; }

  /// <summary>
  /// Gets or sets the timeout duration for synchronization.
  /// </summary>
  /// <value>Default: 5 seconds.</value>
  public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Gets or sets a value indicating whether to use debugger-aware timeouts.
  /// </summary>
  /// <remarks>
  /// When <c>true</c>, timeouts are based on active execution time rather than wall clock time,
  /// preventing false timeouts when paused at breakpoints.
  /// </remarks>
  /// <value>Default: <c>true</c>.</value>
  public bool DebuggerAwareTimeout { get; init; } = true;
}
