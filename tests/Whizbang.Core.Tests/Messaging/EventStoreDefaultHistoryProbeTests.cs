using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The default interface implementation of <see cref="IEventStore.HasStreamEventsBeforeAsync"/>
/// answers "no history".
/// </summary>
/// <remarks>
/// <para>
/// Every store in the repository overrides this member, which is why nothing had ever executed the
/// default body. That is exactly what makes it worth pinning: the default is what a store written
/// outside this repository inherits, and the documented contract on the member says "Default impl
/// returns false". Nothing proved it, so nothing would have caught a change to it.
/// </para>
/// <para>
/// False is the safe answer and the reason is worth stating. The probe asks whether a stream has
/// prior events of the types a perspective folds, and a true answer suppresses a row-retention
/// wake. A store that has not implemented the probe must not suppress anything, so it must answer
/// no rather than yes (issue #696 is the opposite mistake: history the perspective never folded
/// reading as "reaped and woken").
/// </para>
/// </remarks>
[Category("Core")]
[Category("Messaging")]
public class EventStoreDefaultHistoryProbeTests {

  [Test]
  public async Task HasStreamEventsBefore_StoreThatDoesNotOverrideIt_ReturnsFalseAsync(
      CancellationToken cancellationToken) {
    // A store that implements everything EXCEPT the probe, so the call lands on the interface's own
    // body rather than on an override.
    IEventStore store = new StoreWithoutHistoryProbe();

    var hasHistory = await store.HasStreamEventsBeforeAsync(
      Guid.NewGuid(), Guid.NewGuid(), [typeof(ProbeEvent)], cancellationToken);

    await Assert.That(hasHistory).IsFalse()
      .Because("a store that has not implemented the history probe must answer no: the answer gates a "
        + "row-retention wake, and a store that cannot tell must not suppress one. The member documents "
        + "this default and nothing else executes it, so this test is the only thing holding it");
  }

  [Test]
  public async Task HasStreamEventsBefore_DefaultImplementation_IgnoresItsArgumentsAsync(
      CancellationToken cancellationToken) {
    // Pins that the default is a constant rather than something that inspects its inputs: an empty
    // type list and an empty stream get the same answer as a populated one.
    IEventStore store = new StoreWithoutHistoryProbe();

    var withNoTypes = await store.HasStreamEventsBeforeAsync(
      Guid.Empty, Guid.Empty, [], cancellationToken);

    await Assert.That(withNoTypes).IsFalse()
      .Because("the default cannot consult a store it knows nothing about, so it answers the same way "
        + "for every argument; a default that varied would be inventing history");
  }

  private sealed record ProbeEvent : IEvent;

  /// <summary>
  /// A store that implements the abstract surface and nothing else, so every member carrying a
  /// default interface body keeps it. Only the history probe is exercised; the rest exist so the
  /// type compiles, and each throws rather than returning a plausible-looking empty answer, because
  /// a silent default here would let a future test pass while calling the wrong member.
  /// </summary>
  private sealed class StoreWithoutHistoryProbe : IEventStore {
    private static NotSupportedException _notUsed(string member) =>
      new NotSupportedException($"{member} is not part of this test's surface.");

    public Task AppendAsync<TMessage>(
        Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      throw _notUsed(nameof(AppendAsync));

    public Task AppendAsync<TMessage>(
        Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull =>
      throw _notUsed(nameof(AppendAsync));

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
        Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      throw _notUsed(nameof(ReadAsync));

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
        Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      throw _notUsed(nameof(ReadAsync));

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        CancellationToken cancellationToken = default) =>
      throw _notUsed(nameof(ReadPolymorphicAsync));

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
        Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      throw _notUsed(nameof(GetEventsBetweenAsync));

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
        CancellationToken cancellationToken = default) =>
      throw _notUsed(nameof(GetEventsBetweenPolymorphicAsync));

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw _notUsed(nameof(GetLastSequenceAsync));

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
        IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      throw _notUsed(nameof(DeserializeStreamEvents));
  }
}
