namespace Whizbang.Sagas;

/// <summary>
/// Asks for a saga to start, because the saga declared to precede it has finished.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISagaEvent.SagaName"/> is the saga being asked to start, not the one that finished, so
/// a consumer's receptor filters on the name it declared and the parent is described by the
/// properties here. <see cref="ISagaEvent.EntityId"/> is the parent's, because a continuation acts on
/// the same domain entity.
/// </para>
/// <para>
/// A request rather than an initiation. The framework does not know the follow-on saga's item set,
/// and a saga's items are consumer domain, so the consumer's receptor decides what the run contains.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/continuations</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaContinuationTests.cs</tests>
public interface ISagaContinuationRequestedEvent : ISagaEvent {

  /// <summary>Name of the saga that finished and triggered this request.</summary>
  string ParentSagaName { get; }

  /// <summary>Stream id of the saga that finished.</summary>
  Guid ParentSagaId { get; }

  /// <summary>The terminal status the parent reached, which the trigger matched.</summary>
  SagaStatus ParentFinalStatus { get; }
}
