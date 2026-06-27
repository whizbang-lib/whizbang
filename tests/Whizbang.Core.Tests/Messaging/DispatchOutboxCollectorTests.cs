#pragma warning disable CA1707

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the ambient outbox-collector seam (<see cref="DispatchOutboxCollector"/>) used by the composite
/// pre-fanout hook: off by default (Current is null), diverts Add'd messages while open, restores the
/// previous collector on dispose, and supports nesting. The dispatcher-interception behavior (emits land
/// in the collector instead of the outbox) is covered by the Dispatcher + InboxDispatchWorker tests.
/// </summary>
[Category("Messaging")]
public class DispatchOutboxCollectorTests {

  [Test]
  public async Task Current_IsNull_WhenNoCollectorOpenAsync() {
    await Assert.That(DispatchOutboxCollector.Current).IsNull();
  }

  [Test]
  public async Task BeginCollecting_DivertsAddedMessages_ThenRestoresOnDisposeAsync() {
    var a = _outbox("A");
    var b = _outbox("B");

    using (var scope = DispatchOutboxCollector.BeginCollecting()) {
      await Assert.That(DispatchOutboxCollector.Current).IsNotNull();
      DispatchOutboxCollector.Current!.Add(a);
      DispatchOutboxCollector.Current!.Add(b);
      await Assert.That(scope.Collector.Collected.Count).IsEqualTo(2);
      await Assert.That(scope.Collector.Collected[0]).IsSameReferenceAs(a);
      await Assert.That(scope.Collector.Collected[1]).IsSameReferenceAs(b);
    }

    await Assert.That(DispatchOutboxCollector.Current).IsNull()
      .Because("Disposing the collecting scope restores the previously-active collector (null here).");
  }

  [Test]
  public async Task BeginCollecting_NestsAndRestoresOuterAsync() {
    using var outer = DispatchOutboxCollector.BeginCollecting();
    var outerCollector = DispatchOutboxCollector.Current;
    using (DispatchOutboxCollector.BeginCollecting()) {
      await Assert.That(DispatchOutboxCollector.Current).IsNotSameReferenceAs(outerCollector)
        .Because("A nested collector shadows the outer one while open.");
    }
    await Assert.That(DispatchOutboxCollector.Current).IsSameReferenceAs(outerCollector)
      .Because("Disposing the inner scope restores the outer collector.");
  }

  private static OutboxMessage _outbox(string id) => new() {
    MessageId = (Guid)TrackedGuid.NewMedo(),
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse($"{{\"id\":\"{id}\"}}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    },
    Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.E, TestApp]], Whizbang.Core",
    MessageType = "TestApp.E, TestApp",
  };
}
