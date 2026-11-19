// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Contracts;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="InMemoryEventStore"/>: an envelope registry that does not hold
/// the appended message (the store builds its own minimal envelope), and a polymorphic read of an
/// envelope stored without a hop list (the read hands back an empty list, never null).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/InMemoryEventStore.cs</code-under-test>
[Category("Messaging")]
public class InMemoryEventStoreBranchCoverageTests {

  [Test]
  public async Task AppendAsync_WithMessage_RegistryWithoutThatMessage_CreatesAMinimalEnvelopeAsync() {
    using var registry = new EnvelopeRegistry();
    var eventStore = new InMemoryEventStore(registry);
    var streamId = Guid.NewGuid();

    await eventStore.AppendAsync(streamId, new TestEvent { StreamId = streamId, Payload = "unregistered" });

    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }
    await Assert.That(events.Count).IsEqualTo(1)
      .Because("a registry that never saw the message is no reason to drop it");
    await Assert.That(events[0].Payload.Payload).IsEqualTo("unregistered");
    await Assert.That(events[0].Hops.Count).IsEqualTo(1);
    await Assert.That(events[0].Hops[0].ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown)
      .Because("with no registered envelope to preserve, the store stamps the minimal unknown-origin hop");
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_EnvelopeStoredWithoutHops_ReturnsAnEmptyHopListAsync() {
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    await eventStore.AppendAsync(streamId, new MessageEnvelope<TestEvent> {
      MessageId = messageId,
      Payload = new TestEvent { StreamId = streamId, Payload = "no-hops" },
      Hops = null!,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    });

    var read = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId, afterEventId: null, upToEventId: Guid.Empty, eventTypes: [typeof(TestEvent)]);

    await Assert.That(read.Count).IsEqualTo(1);
    await Assert.That(read[0].MessageId).IsEqualTo(messageId);
    await Assert.That(read[0].Hops).IsNotNull()
      .Because("consumers walk the hop list without a null check, so a missing list must read as empty");
    await Assert.That(read[0].Hops.Count).IsEqualTo(0);
  }
}
