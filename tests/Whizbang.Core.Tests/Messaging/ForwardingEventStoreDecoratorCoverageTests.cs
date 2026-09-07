using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers <see cref="ForwardingEventStoreDecorator.DeserializeStreamEvents"/> — the sibling
/// <c>EventStoreDecoratorForwardingTests</c> proves every real decorator forwards every
/// default-interface member, but never actually calls <c>DeserializeStreamEvents</c> through the
/// base class's own passthrough (its <c>ProbeAwareStore</c> fake never gets asked for it).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ForwardingEventStoreDecorator.cs</code-under-test>
[Category("Messaging")]
public class ForwardingEventStoreDecoratorCoverageTests {

  [Test]
  public async Task DeserializeStreamEvents_ForwardsToInnerStore_ReturningItsExactResultAsync() {
    // If this passthrough regressed to returning [] locally instead of delegating, every decorated
    // store would silently lose whatever deserialization behavior (upcasting, polymorphic
    // dispatch) the inner store implements.
    var sentinel = new List<MessageEnvelope<IEvent>> { new(MessageId.New(), new _probeEvent("x"), []) };
    var inner = new _probeStore(sentinel);
    var decorator = new _passthroughDecorator(inner);

    var result = decorator.DeserializeStreamEvents([], []);

    await Assert.That(result).IsSameReferenceAs(sentinel)
      .Because("the base class does not implement deserialization itself — it must return the inner store's exact result.");
  }

  private sealed record _probeEvent(string Data) : IEvent;

  /// <summary>Concrete decorator that overrides nothing — every member rides the base passthrough.</summary>
  private sealed class _passthroughDecorator(IEventStore inner) : ForwardingEventStoreDecorator(inner);

  private sealed class _probeStore(List<MessageEnvelope<IEvent>> deserializeResult) : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull =>
      Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      deserializeResult;
  }
}
