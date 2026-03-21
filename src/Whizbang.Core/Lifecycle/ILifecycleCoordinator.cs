using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Centralized coordinator for event lifecycle stage transitions.
/// Owns receptor invocation, tag processing, and ImmediateAsync chaining.
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
  /// Called at cascade time when DispatchMode determines multiple paths.
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
