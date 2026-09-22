namespace Whizbang.Sagas;

/// <summary>
/// Concrete request the framework emits from <c>BaseSagaService.CompleteSagaAsync</c> when a finished
/// saga declared a continuation whose trigger matches its final status.
/// </summary>
/// <remarks>
/// <para>
/// Concrete, following <see cref="SagaCompletionAbandonedEvent"/>, so the framework can construct it
/// without every consumer supplying a per-saga subclass. Carrying <see cref="SagaName"/> and
/// <see cref="EntityId"/> like the rest of the lifecycle keeps it shape-compatible, which means the
/// existing message-registry routing handles it with no special case.
/// </para>
/// <para>
/// <see cref="SagaName"/> is the saga being asked to start. A consumer writes one receptor on this
/// type, filters on the name it declared as a continuation, and initiates that saga for
/// <see cref="EntityId"/>.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/continuations</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaContinuationTests.cs</tests>
public class SagaContinuationRequestedEvent : SagaEventBase, ISagaContinuationRequestedEvent {

  /// <summary>The name of the saga being asked to start.</summary>
  public string SagaName { get; set; } = string.Empty;

  /// <summary>The parent's entity id, carried because the continuation acts on the same entity.</summary>
  public Guid EntityId { get; set; }

  /// <summary>Stream id this request is bound to (the finished saga's stream).</summary>
  public Guid StreamId { get; set; }

  /// <summary>Name of the saga that finished.</summary>
  public string ParentSagaName { get; set; } = string.Empty;

  /// <summary>Stream id of the saga that finished.</summary>
  public Guid ParentSagaId { get; set; }

  /// <summary>The terminal status the parent reached.</summary>
  public SagaStatus ParentFinalStatus { get; set; }
}
