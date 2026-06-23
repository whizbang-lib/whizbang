using Whizbang.Core;

namespace Whizbang.Sagas.Services;

/// <summary>
/// Narrow emission surface that
/// <see cref="BaseSagaService{TInit, TItemsDispatched, TItemStarted, TItemCompleted, TItemFailed, TCompleted, TReset, THookStarted, THookCompleted}"/>
/// publishes events through. Decouples the saga service from
/// <c>IDispatcher</c>'s 27-method API so the saga library can be
/// unit-tested in isolation and so a future consumer that wants to
/// route saga events through a non-dispatcher pipeline (e.g. an
/// in-memory simulator) can swap the implementation.
/// </summary>
public interface ISagaEventEmitter {

  /// <summary>Publishes an event through the dispatcher's normal path. Used for non-terminal saga events.</summary>
  Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent;

  /// <summary>
  /// Publishes an event with an optional <paramref name="scheduledFor"/> wake-up time. Implementations
  /// that bridge to <c>IDispatcher</c> wire this through <c>DispatchOptions.ScheduledFor</c> so
  /// <c>wh_outbox.scheduled_for</c> gates pickup until the instant elapses (mig 040 +
  /// mig 049 NOTIFY wake-up). Default-interface-method fallback ignores the schedule and emits
  /// immediately — preserves backward compatibility for existing test fixtures and any consumer
  /// implementation that hasn't yet adopted the scheduled-emission surface.
  /// </summary>
  /// <remarks>
  /// Used by the saga framework's completion watchdog: <c>BaseSagaService.InitiateSagaAsync</c>
  /// arms the first tick at "expected completion + slack" via this overload so the framework's
  /// recovery loop doesn't burn through ticks before the saga has had a chance to complete.
  /// </remarks>
  Task PublishAsync<TEvent>(TEvent eventData, DateTimeOffset? scheduledFor) where TEvent : IEvent
    => PublishAsync(eventData);

  /// <summary>
  /// Publishes an event exactly once per <paramref name="claimKey"/>.
  /// Used for the terminal saga completion event so N concurrent
  /// completion handlers collapse to exactly one emission.
  /// </summary>
  Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent;
}
