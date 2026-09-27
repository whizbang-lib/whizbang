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
/// <docs>fundamentals/sagas/whizbang-sagas</docs>
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

  /// <summary>
  /// Publishes at most once per <paramref name="claimKey"/>, as the system acting in
  /// <paramref name="tenantId"/>.
  /// </summary>
  /// <remarks>
  /// For background work with no request of its own, such as the stranded-saga sweep: the claim makes
  /// every instance and restart arrive at one emission, and the tenant makes the event handled where
  /// it belongs. <see langword="null"/> publishes for all tenants. The default ignores the tenant and
  /// claims as <see cref="PublishOnceAsync{TEvent}"/> does, for an emitter with no notion of scope.
  /// </remarks>
  /// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/DispatcherSagaEventEmitterTests.cs</tests>
  Task<bool> PublishOnceInTenantAsync<TEvent>(string? tenantId, string claimKey, TEvent eventData, CancellationToken cancellationToken)
      where TEvent : IEvent
    => PublishOnceAsync(claimKey, eventData, cancellationToken);


  /// <summary>
  /// Runs <paramref name="work"/> as the system acting in <paramref name="tenantId"/>, so reads inside it
  /// see that tenant as the ambient scope.
  /// </summary>
  /// <remarks>
  /// For background work with no request of its own that must read a tenant's records before it
  /// publishes, such as the stranded-saga sweep: a repository reading through a tenant-scoped lens
  /// refuses to run without an ambient tenant. <see langword="null"/> runs the work in the caller's own
  /// context, unchanged. The default ignores the tenant and runs the work as is, for an emitter with no
  /// notion of scope.
  /// </remarks>
  /// <typeparam name="TResult">What the work returns.</typeparam>
  /// <param name="tenantId">The tenant to run in, or <see langword="null"/> for the caller's context.</param>
  /// <param name="work">The work to run; receives <paramref name="cancellationToken"/>.</param>
  /// <param name="cancellationToken">Passed to the work.</param>
  /// <returns>The work's result.</returns>
  /// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:EmitterDefault_RunInTenant_RunsTheWorkAsIsAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:EmitterDefault_RunInTenant_NullWork_ThrowsAsync</tests>
  Task<TResult> RunInTenantAsync<TResult>(string? tenantId, Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(work);
    return work(cancellationToken);
  }
}
