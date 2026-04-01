using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Centralized coordinator for event lifecycle stage transitions.
/// Owns receptor invocation, tag processing, and ImmediateDetached chaining.
/// Tracks live events and fires hooks exactly once per stage.
/// </summary>
/// <remarks>
/// <para>Tracking lifecycle:</para>
/// <list type="bullet">
/// <item><description>Created at entry points (dispatch, DB load, transport receive)</description></item>
/// <item><description>Advanced through stages by the owning worker</description></item>
/// <item><description>Abandoned at exit points (DB persist, transport send, processing complete)</description></item>
/// </list>
/// <para>Thread-safe. Supports concurrent tracking of multiple events.</para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator</docs>
public interface ILifecycleCoordinator {
  /// <summary>
  /// Begins tracking an event at the specified entry stage.
  /// Returns a tracking handle for advancing through stages.
  /// </summary>
  /// <param name="eventId">Unique event identifier.</param>
  /// <param name="envelope">The message envelope containing the payload and metadata.</param>
  /// <param name="entryStage">The lifecycle stage at which tracking begins.</param>
  /// <param name="source">The message source (Local, Outbox, Inbox).</param>
  /// <param name="streamId">Optional stream ID for perspective-based processing.</param>
  /// <param name="perspectiveType">Optional perspective type being processed.</param>
  /// <returns>A tracking handle for advancing through stages.</returns>
  ILifecycleTracking BeginTracking(
    Guid eventId,
    IMessageEnvelope envelope,
    LifecycleStage entryStage,
    MessageSource source,
    Guid? streamId = null,
    Type? perspectiveType = null);

  /// <summary>
  /// Gets the current tracking state for an event, if tracked.
  /// Enables runtime inspection of event lifecycle position.
  /// </summary>
  /// <param name="eventId">The event ID to look up.</param>
  /// <returns>The tracking handle if the event is currently tracked; null otherwise.</returns>
  ILifecycleTracking? GetTracking(Guid eventId);

  /// <summary>
  /// Registers expected completion sources for WhenAll pattern.
  /// Called at cascade time when DispatchModes determines multiple paths.
  /// PostLifecycle fires only when all expected sources signal completion.
  /// </summary>
  /// <param name="eventId">The event ID to register completions for.</param>
  /// <param name="sources">The completion sources expected before PostLifecycle fires.</param>
  void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources);

  /// <summary>
  /// Signals that a processing path completed for the event.
  /// If WhenAll is active and all sources complete, fires PostLifecycle.
  /// If no WhenAll, fires PostLifecycle immediately.
  /// </summary>
  /// <param name="eventId">The event ID that completed a processing segment.</param>
  /// <param name="source">Which processing path completed.</param>
  /// <param name="scopedProvider">Scoped service provider for receptor resolution.</param>
  /// <param name="ct">Cancellation token.</param>
  ValueTask SignalSegmentCompleteAsync(
    Guid eventId,
    PostLifecycleCompletionSource source,
    IServiceProvider scopedProvider,
    CancellationToken ct);

  /// <summary>
  /// Abandons tracking for an event (exit point).
  /// Called when event leaves live processing (persisted, sent to transport, complete).
  /// </summary>
  /// <param name="eventId">The event ID to stop tracking.</param>
  void AbandonTracking(Guid eventId);

  /// <summary>
  /// Registers which perspectives must complete before PostLifecycle fires.
  /// Tracked by exact (eventId, perspectiveName) pairs.
  /// Idempotent — calling again with the same eventId is a no-op.
  /// </summary>
  /// <param name="eventId">The event ID to register perspective completions for.</param>
  /// <param name="perspectiveNames">The perspective names that must complete.</param>
  void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames);

  /// <summary>
  /// Signals that a specific perspective completed processing an event.
  /// Returns true if this was the LAST perspective (all expected are now complete).
  /// </summary>
  /// <param name="eventId">The event ID that the perspective completed.</param>
  /// <param name="perspectiveName">The name of the perspective that completed.</param>
  /// <returns>True if all expected perspectives are now complete; false otherwise.</returns>
  bool SignalPerspectiveComplete(Guid eventId, string perspectiveName);

  /// <summary>
  /// Checks if all expected perspectives have completed for an event.
  /// Returns true if no expectations are registered — terminal stages must always fire.
  /// </summary>
  /// <param name="eventId">The event ID to check.</param>
  /// <returns>True if all expected perspectives have signaled complete, or no expectations exist.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleCoordinatorTests.cs:AreAllPerspectivesComplete_NoExpectationsRegistered_ReturnsTrueAsync</tests>
  bool AreAllPerspectivesComplete(Guid eventId);

  /// <summary>
  /// Removes tracking entries that have been inactive longer than the specified threshold.
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleCoordinatorTests.cs:CleanupStaleTracking_RemovesInactiveEntries_WhenOlderThanThresholdAsync</tests>
  /// Uses a debounce-style sliding window — each stage transition and perspective signal
  /// resets the inactivity timer, so active events are never cleaned up.
  /// </summary>
  /// <param name="inactivityThreshold">How long an entry must be inactive before cleanup.</param>
  /// <returns>Number of stale entries removed.</returns>
  int CleanupStaleTracking(TimeSpan inactivityThreshold);
}

/// <summary>
/// Identifies a processing path that must complete before PostLifecycle fires.
/// Used with the WhenAll pattern for events that traverse multiple paths.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#whenall</docs>
public enum PostLifecycleCompletionSource {
  /// <summary>Local dispatch path completed.</summary>
  Local,

  /// <summary>Distributed path completed (outbox → inbox → perspectives).</summary>
  Distributed,

  /// <summary>Outbox publishing completed (event left service).</summary>
  Outbox
}
