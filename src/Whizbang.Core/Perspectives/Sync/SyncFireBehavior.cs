namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Controls when the handler is invoked relative to sync status.
/// </summary>
/// <remarks>
/// <para>
/// This enum determines what happens after waiting for perspective synchronization:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="FireOnSuccess"/>: Only invoke handler if sync completes. Throw on timeout.</description></item>
/// <item><description><see cref="FireAlways"/>: Invoke handler regardless of outcome. Use <see cref="SyncContext"/> for status.</description></item>
/// <item><description><see cref="FireOnEachEvent"/>: Future streaming mode for event-by-event processing.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#fire-behavior</docs>
public enum SyncFireBehavior {
  /// <summary>
  /// Only invoke handler if sync completes successfully. Throw on timeout.
  /// </summary>
  /// <remarks>
  /// This is the default behavior. If the perspective does not sync within the timeout,
  /// a <see cref="PerspectiveSyncTimeoutException"/> is thrown.
  /// </remarks>
  FireOnSuccess = 0,

  /// <summary>
  /// Invoke handler regardless of sync outcome. Use <see cref="SyncContext"/> for status.
  /// </summary>
  /// <remarks>
  /// The handler is always invoked, even on timeout. Inject <see cref="SyncContext"/>
  /// to inspect the sync outcome and handle stale data appropriately.
  /// </remarks>
  FireAlways = 1,

  /// <summary>
  /// Invoke handler on each event completion (streaming mode - future).
  /// </summary>
  /// <remarks>
  /// Reserved for future use. Will enable streaming scenarios where the handler
  /// is invoked multiple times as each event is processed.
  /// </remarks>
  FireOnEachEvent = 2
}
