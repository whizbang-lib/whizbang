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
/// // Wait for specific event types (default: throw on timeout)
/// [AwaitPerspectiveSync(typeof(OrderPerspective),
///     EventTypes = [typeof(OrderCreatedEvent)])]
/// public class NotificationHandler : IReceptor&lt;OrderCreatedEvent&gt; {
///     // Handler code - only runs if sync completes
/// }
///
/// // Wait for all events, but always fire handler (check SyncContext for outcome)
/// [AwaitPerspectiveSync(typeof(OrderPerspective),
///     FireBehavior = SyncFireBehavior.FireAlways)]
/// public class GracefulHandler : IReceptor&lt;OrderCreatedEvent&gt; {
///     public GracefulHandler(SyncContext? syncContext) {
///         if (syncContext?.IsTimedOut == true) {
///             // Handle stale data scenario
///         }
///     }
/// }
/// </code>
/// <para>
/// All synchronization uses database-based lookup via the batch function.
/// The database is the only authority for determining when perspectives have processed events.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/AwaitPerspectiveSyncAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class AwaitPerspectiveSyncAttribute(Type perspectiveType) : Attribute {
  /// <summary>
  /// Gets or sets the default timeout in milliseconds for all sync operations.
  /// </summary>
  /// <remarks>
  /// This static property allows global configuration of the default timeout.
  /// Individual attributes can override this via <see cref="TimeoutMs"/>.
  /// </remarks>
  /// <value>Default: 5000 (5 seconds).</value>
  public static int DefaultTimeoutMs { get; set; } = 5000;

  /// <summary>
  /// Gets the type of the perspective to wait for.
  /// </summary>
  public Type PerspectiveType { get; } = perspectiveType ?? throw new ArgumentNullException(nameof(perspectiveType));

  /// <summary>
  /// Gets or sets the event types to wait for.
  /// </summary>
  /// <remarks>
  /// If null or empty, waits for ALL pending events on the stream
  /// regardless of event type.
  /// </remarks>
  public Type[]? EventTypes { get; init; }

  /// <summary>
  /// Gets or sets the timeout in milliseconds for this specific sync operation.
  /// </summary>
  /// <remarks>
  /// Set to -1 (default) to use <see cref="DefaultTimeoutMs"/>.
  /// Set to 0 or a positive value to override the default.
  /// Use <see cref="EffectiveTimeoutMs"/> to get the actual timeout that will be used.
  /// </remarks>
  /// <value>Default: -1 (use <see cref="DefaultTimeoutMs"/>).</value>
  public int TimeoutMs { get; init; } = -1;

  /// <summary>
  /// Gets the effective timeout in milliseconds that will be used for sync.
  /// </summary>
  /// <remarks>
  /// Returns <see cref="TimeoutMs"/> if explicitly set (not -1),
  /// otherwise returns <see cref="DefaultTimeoutMs"/>.
  /// </remarks>
  public int EffectiveTimeoutMs => TimeoutMs == -1 ? DefaultTimeoutMs : TimeoutMs;

  /// <summary>
  /// Gets or sets the behavior when sync completes or times out.
  /// </summary>
  /// <remarks>
  /// <list type="bullet">
  /// <item><description><see cref="SyncFireBehavior.FireOnSuccess"/>: Only invoke handler if sync completes. Throw on timeout.</description></item>
  /// <item><description><see cref="SyncFireBehavior.FireAlways"/>: Always invoke handler. Use <see cref="SyncContext"/> for status.</description></item>
  /// <item><description><see cref="SyncFireBehavior.FireOnEachEvent"/>: Future streaming mode.</description></item>
  /// </list>
  /// </remarks>
  /// <value>Default: <see cref="SyncFireBehavior.FireOnSuccess"/>.</value>
  public SyncFireBehavior FireBehavior { get; init; } = SyncFireBehavior.FireOnSuccess;
}
