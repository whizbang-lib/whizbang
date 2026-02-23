namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Marks a receptor to wait for perspective synchronization before execution.
/// </summary>
/// <remarks>
/// <para>
/// When applied to a receptor class, the receptor invoker will wait for the specified
/// perspective to be caught up before invoking the handler.
/// </para>
/// <para>
/// <strong>Usage Examples:</strong>
/// </para>
/// <code>
/// // Wait for specific event types
/// [AwaitPerspectiveSync(typeof(OrderPerspective),
///     EventTypes = [typeof(OrderCreatedEvent)],
///     LookupMode = SyncLookupMode.Local)]
/// public class NotificationHandler : IReceptor&lt;OrderCreatedEvent&gt; {
///     // Handler code
/// }
///
/// // Wait for all events (inferred from perspective)
/// [AwaitPerspectiveSync(typeof(OrderPerspective))]
/// public class FullSyncHandler : IReceptor&lt;OrderCreatedEvent&gt; {
///     // Handler code
/// }
/// </code>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/AwaitPerspectiveSyncAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class AwaitPerspectiveSyncAttribute : Attribute {
  /// <summary>
  /// Initializes a new instance of <see cref="AwaitPerspectiveSyncAttribute"/>.
  /// </summary>
  /// <param name="perspectiveType">The type of the perspective to wait for.</param>
  public AwaitPerspectiveSyncAttribute(Type perspectiveType) {
    PerspectiveType = perspectiveType ?? throw new ArgumentNullException(nameof(perspectiveType));
  }

  /// <summary>
  /// Gets the type of the perspective to wait for.
  /// </summary>
  public Type PerspectiveType { get; }

  /// <summary>
  /// Gets or sets the event types to wait for.
  /// </summary>
  /// <remarks>
  /// If null or empty, waits for ALL event types that the perspective handles
  /// (discovered via IPerspectiveFor interfaces).
  /// </remarks>
  public Type[]? EventTypes { get; init; }

  /// <summary>
  /// Gets or sets the lookup mode for finding pending events.
  /// </summary>
  /// <value>Default: <see cref="SyncLookupMode.Local"/>.</value>
  public SyncLookupMode LookupMode { get; init; } = SyncLookupMode.Local;

  /// <summary>
  /// Gets or sets the timeout in milliseconds.
  /// </summary>
  /// <value>Default: 5000 (5 seconds).</value>
  public int TimeoutMs { get; init; } = 5000;

  /// <summary>
  /// Gets or sets whether to throw an exception on timeout.
  /// </summary>
  /// <remarks>
  /// If <c>false</c>, the handler proceeds with eventual consistency on timeout.
  /// </remarks>
  /// <value>Default: <c>false</c>.</value>
  public bool ThrowOnTimeout { get; init; }

  /// <summary>
  /// Converts this attribute to <see cref="PerspectiveSyncOptions"/>.
  /// </summary>
  /// <returns>The sync options configured from this attribute.</returns>
  public PerspectiveSyncOptions ToSyncOptions() {
    SyncFilterNode filter = EventTypes is { Length: > 0 }
        ? new EventTypeFilter(EventTypes)
        : new AllPendingFilter();

    return new PerspectiveSyncOptions {
      Filter = filter,
      LookupMode = LookupMode,
      Timeout = TimeSpan.FromMilliseconds(TimeoutMs),
      DebuggerAwareTimeout = true
    };
  }
}
