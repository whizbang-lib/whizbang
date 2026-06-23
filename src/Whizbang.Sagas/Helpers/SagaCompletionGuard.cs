using Whizbang.Core;

namespace Whizbang.Sagas.Helpers;

/// <summary>
/// Thin wrapper around <see cref="IDispatcher.PublishOnceAsync{TEvent}(string, TEvent, CancellationToken)"/>
/// applying the saga-claim-key convention. Concentrates the "what's
/// the claim key for a saga completion?" decision in one place so
/// every emitter (and every saga service) routes through the same
/// string, eliminating inter-saga collisions without each call site
/// remembering to prefix.
/// </summary>
/// <remarks>
/// <para>
/// Convention: <c>"saga-completed:{sagaName}:{sagaId}"</c>. The
/// <c>sagaName</c> segment is what makes the key safe across saga
/// types — two different sagas happening to share a stream id
/// (unlikely but not impossible) won't collide on the dispatcher's
/// claim row.
/// </para>
/// <para>
/// The whole point is that the dispatcher's <c>PublishOnceAsync</c>
/// collapses concurrent emitters to exactly one via atomic INSERT …
/// ON CONFLICT DO NOTHING. The guard exists to package the saga
/// idiom — receptors that previously wrote
/// <c>if (await CompletionGuard.AlreadyEmittedAsync(...)) return;
/// await dispatcher.PublishAsync(...);</c> now write
/// <c>await SagaCompletionGuard.EmitOnceAsync(dispatcher, sagaName,
/// sagaId, evt, ct);</c> — one line, no read-then-act window.
/// </para>
/// </remarks>
public static class SagaCompletionGuard {

  /// <summary>
  /// Builds the saga claim key for the dispatcher. Public so consumers
  /// can use it directly when they need to call
  /// <see cref="IDispatcher.PublishOnceAsync"/> with the saga
  /// convention but don't want the rest of <see cref="EmitOnceAsync"/>'s
  /// shape.
  /// </summary>
  public static string ClaimKey(string sagaName, Guid sagaId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
    return $"saga-completed:{sagaName}:{sagaId}";
  }

  /// <summary>
  /// Emits the saga's terminal event at most once. Returns <c>true</c>
  /// if this caller won the claim and the event was published;
  /// <c>false</c> if another caller already won it. The intentional
  /// no-op return (no thrown conflict) is the design — receptors
  /// racing on saga completion just see false and quietly proceed.
  /// </summary>
  public static Task<bool> EmitOnceAsync<TEvent>(
      IDispatcher dispatcher,
      string sagaName,
      Guid sagaId,
      TEvent eventData,
      CancellationToken cancellationToken) where TEvent : IEvent {
    ArgumentNullException.ThrowIfNull(dispatcher);
    return dispatcher.PublishOnceAsync(ClaimKey(sagaName, sagaId), eventData, cancellationToken);
  }
}
