namespace Whizbang.Sagas;

/// <summary>
/// A saga event stored on its saga's own stream, the one keyed by <see cref="SagaId"/>.
/// </summary>
/// <remarks>
/// <para>
/// A saga has one stream key, the saga id: the projection loader reads by it, per-item streams derive
/// from it, and the watchdog tick, the abandonment and the continuation request are stored on it. The
/// entity id is the consumer's domain identity, carried for filtering and routing, and frequently but
/// not necessarily the same value.
/// </para>
/// <para>
/// Every event a <c>[Saga]</c> declaration generates implements this, and the saga framework resolves
/// its stream from it, since no consumer's source generator can see those generated classes. A
/// hand-written saga event that implements it is stored the same way.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/whizbang-sagas#stream-key</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaStreamKeyTests.cs</tests>
public interface ISagaStreamEvent : ISagaEvent {

  /// <summary>The saga's stream id: the stream this event is stored on.</summary>
  Guid SagaId { get; set; }
}
