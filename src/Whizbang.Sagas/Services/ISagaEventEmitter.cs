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
  /// Publishes an event exactly once per <paramref name="claimKey"/>.
  /// Used for the terminal saga completion event so N concurrent
  /// completion handlers collapse to exactly one emission.
  /// </summary>
  Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent;
}
