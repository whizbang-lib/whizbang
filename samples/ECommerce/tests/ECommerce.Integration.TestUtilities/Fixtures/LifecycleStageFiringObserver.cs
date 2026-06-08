using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace ECommerce.Integration.TestUtilities.Fixtures;

/// <summary>
/// Test-side <see cref="IReceptorFiringObserver"/> that provides deterministic
/// signal-based synchronization for lifecycle integration tests.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the prior <see cref="GenericLifecycleCompletionReceptor{T}"/>
/// per-test receptor pattern (which relied on the receptor's own
/// <c>HandleAsync</c> as the completion signal — fragile under shared-fixture
/// state, AsyncLocal context propagation, perspective-vs-receptor stage
/// ordering, and parallel-pressure scheduling delays).
/// </para>
/// <para>
/// Registered once per host as singleton; the <see cref="ReceptorInvoker"/>
/// resolves it via DI and invokes
/// <see cref="IReceptorFiringObserver.OnReceptorFiringAsync"/> immediately
/// before EVERY receptor delegate fires — including production tag hooks,
/// generated perspective stage receptors, and any other registered receptor.
/// The observer matches per-test registrations against the firing
/// <c>(stage, message type, payload predicate)</c> and signals a TCS
/// deterministically.
/// </para>
/// <para>
/// To guarantee at least one receptor exists at the target stage (otherwise
/// the invoker short-circuits and never calls <c>OnReceptorFiringAsync</c>),
/// tests still need to register some receptor at the stage; the observer's
/// signal IS the completion gate, not the receptor's body. Use
/// <see cref="NoOpReceptor{TMessage}"/> for the placeholder when no real
/// receptor exists at that stage.
/// </para>
/// </remarks>
public sealed class LifecycleStageFiringObserver : IReceptorFiringObserver {
  private readonly List<_Wait> _waits = [];
  private readonly Lock _lock = new();

  private sealed record _Wait(
    LifecycleStage Stage,
    Type MessageType,
    Func<object, bool>? Filter,
    TaskCompletionSource<IMessageEnvelope> Tcs
  );

  /// <inheritdoc />
  public ValueTask OnReceptorFiringAsync(
      string receptorId,
      LifecycleStage stage,
      Guid messageId,
      IMessageEnvelope envelope,
      CancellationToken cancellationToken)
    => ValueTask.CompletedTask;

  /// <inheritdoc />
  /// <remarks>
  /// Signals on <see cref="OnReceptorFiredAsync"/> rather than
  /// <see cref="OnReceptorFiringAsync"/> so that any state mutated inside the
  /// receptor's <c>HandleAsync</c> (e.g.,
  /// <c>GenericLifecycleCompletionReceptor.InvocationCount</c>) is fully
  /// visible to the awaiting test by the time the TCS completes.
  /// </remarks>
  public ValueTask OnReceptorFiredAsync(
      string receptorId,
      LifecycleStage stage,
      Guid messageId,
      IMessageEnvelope envelope,
      TimeSpan duration,
      Exception? exception,
      CancellationToken cancellationToken) {
    var payload = envelope.Payload;
    var payloadType = payload.GetType();

    List<_Wait> matches;
    lock (_lock) {
      matches = _waits
        .Where(w =>
          w.Stage == stage
          && w.MessageType.IsAssignableFrom(payloadType)
          && (w.Filter is null || w.Filter(payload))
        )
        .ToList();
    }

    foreach (var w in matches) {
      w.Tcs.TrySetResult(envelope);
    }

    return ValueTask.CompletedTask;
  }

  /// <summary>
  /// Registers a wait for the next <see cref="OnReceptorFiringAsync"/>
  /// invocation that matches <paramref name="stage"/>, has a payload assignable
  /// from <typeparamref name="TEvent"/>, and satisfies
  /// <paramref name="messageFilter"/> (when non-null). The returned task
  /// completes with the matching envelope, or throws
  /// <see cref="OperationCanceledException"/> when
  /// <paramref name="cancellationToken"/> fires.
  /// </summary>
  public Task<IMessageEnvelope> WaitForStageAsync<TEvent>(
      LifecycleStage stage,
      Func<TEvent, bool>? messageFilter = null,
      CancellationToken cancellationToken = default)
    where TEvent : IMessage {
    var tcs = new TaskCompletionSource<IMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
    Func<object, bool>? boxedFilter = messageFilter is null
      ? null
      : (object o) => o is TEvent t && messageFilter(t);

    lock (_lock) {
      _waits.Add(new _Wait(stage, typeof(TEvent), boxedFilter, tcs));
    }

    return tcs.Task.WaitAsync(cancellationToken);
  }
}

/// <summary>
/// Trivial receptor that does nothing — its only purpose is to give the
/// <see cref="ReceptorInvoker"/> something to iterate so
/// <see cref="LifecycleStageFiringObserver.OnReceptorFiringAsync"/> is
/// invoked at the target stage.
/// </summary>
public sealed class NoOpReceptor<TMessage> : IReceptor<TMessage> where TMessage : IMessage {
  /// <inheritdoc />
  public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default)
    => ValueTask.CompletedTask;
}
