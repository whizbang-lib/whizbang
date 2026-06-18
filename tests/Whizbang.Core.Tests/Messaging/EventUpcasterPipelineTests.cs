using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for the event-upcasting pipeline — the ordered, AOT-safe transform that runs each
/// stored event through registered <see cref="IEventUpcaster"/>s after deserialization and
/// before routing/Apply. Pure (Rule 10): no reflection, no I/O, deterministic.
/// </summary>
public class EventUpcasterPipelineTests {
  [Test]
  public async Task Apply_WithNoUpcasters_ReturnsEventUnchangedAsync() {
    var pipeline = new EventUpcasterPipeline([]);
    var original = new OrderV1Event { StreamId = Guid.NewGuid(), Data = "x" };

    var result = pipeline.Apply(original);

    await Assert.That(pipeline.HasAny).IsFalse();
    await Assert.That(result).IsSameReferenceAs(original);
  }

  [Test]
  public async Task Apply_WithMatchingUpcaster_TransformsEventAsync() {
    var pipeline = new EventUpcasterPipeline([new V1ToV2Upcaster()]);
    var v1 = new OrderV1Event { StreamId = Guid.NewGuid(), Data = "payload" };

    var result = pipeline.Apply(v1);

    await Assert.That(result).IsTypeOf<OrderV2Event>();
    var v2 = (OrderV2Event)result;
    await Assert.That(v2.Data).IsEqualTo("payload");
    await Assert.That(v2.Version).IsEqualTo(2);   // backfilled default
    await Assert.That(v2.StreamId).IsEqualTo(v1.StreamId);
  }

  [Test]
  public async Task Apply_WithNonMatchingUpcaster_LeavesEventUntouchedAsync() {
    var pipeline = new EventUpcasterPipeline([new V1ToV2Upcaster()]);
    var unrelated = new UnrelatedEvent { StreamId = Guid.NewGuid() };

    var result = pipeline.Apply(unrelated);

    await Assert.That(result).IsSameReferenceAs(unrelated);
  }

  [Test]
  public async Task Apply_ChainsUpcastersInRegistrationOrder_V1ToV3Async() {
    // Composition: V1->V2 then V2->V3 in a single forward pass.
    var pipeline = new EventUpcasterPipeline([new V1ToV2Upcaster(), new V2ToV3Upcaster()]);
    var v1 = new OrderV1Event { StreamId = Guid.NewGuid(), Data = "d" };

    var result = pipeline.Apply(v1);

    await Assert.That(result).IsTypeOf<OrderV3Event>();
    await Assert.That(((OrderV3Event)result).Data).IsEqualTo("d");
  }

  [Test]
  public async Task Apply_RegistrationOrderMatters_ReverseOrderStopsAtV2Async() {
    // Registered newest->oldest: the single forward pass can't reach V3 from a V1 input.
    // Documents that upcasters must be registered oldest-shape-first.
    var pipeline = new EventUpcasterPipeline([new V2ToV3Upcaster(), new V1ToV2Upcaster()]);
    var v1 = new OrderV1Event { StreamId = Guid.NewGuid(), Data = "d" };

    var result = pipeline.Apply(v1);

    await Assert.That(result).IsTypeOf<OrderV2Event>();
  }

  [Test]
  public async Task Apply_RekeyUpcaster_MutatesStreamIdInPlaceAsync() {
    // The re-key shape (RFC): cast to the known concrete type, set [StreamId], return same instance.
    var pipeline = new EventUpcasterPipeline([new RekeyUpcaster()]);
    var ev = new RekeyableEvent { StreamId = Guid.NewGuid(), SagaId = Guid.NewGuid(), Item = "abc" };
    var newKey = RekeyUpcaster.KeyFor(ev.SagaId, ev.Item);

    var result = pipeline.Apply(ev);

    await Assert.That(result).IsSameReferenceAs(ev);
    await Assert.That(((RekeyableEvent)result).StreamId).IsEqualTo(newKey);
  }

  [Test]
  public async Task Apply_WithNullEvent_ThrowsAsync() {
    var pipeline = new EventUpcasterPipeline([new V1ToV2Upcaster()]);

    await Assert.That(() => pipeline.Apply(null!)).Throws<ArgumentNullException>();
  }

  // ── test events (each carries [StreamId] to satisfy WHIZ009) ──
#pragma warning disable WHIZ009
  public record OrderV1Event : IEvent {
    [StreamId] public Guid StreamId { get; set; }
    public string Data { get; set; } = string.Empty;
  }

  public record OrderV2Event : IEvent {
    [StreamId] public Guid StreamId { get; set; }
    public string Data { get; set; } = string.Empty;
    public int Version { get; set; }
  }

  public record OrderV3Event : IEvent {
    [StreamId] public Guid StreamId { get; set; }
    public string Data { get; set; } = string.Empty;
  }

  public record UnrelatedEvent : IEvent {
    [StreamId] public Guid StreamId { get; set; }
  }

  public record RekeyableEvent : IEvent {
    [StreamId] public Guid StreamId { get; set; }
    public Guid SagaId { get; set; }
    public string Item { get; set; } = string.Empty;
  }
#pragma warning restore WHIZ009

  // ── test upcasters ──
  private sealed class V1ToV2Upcaster : IEventUpcaster {
    public bool CanUpcast(IEvent @event) => @event is OrderV1Event;
    public IEvent Upcast(IEvent @event) {
      var v1 = (OrderV1Event)@event;
      return new OrderV2Event { StreamId = v1.StreamId, Data = v1.Data, Version = 2 };
    }
  }

  private sealed class V2ToV3Upcaster : IEventUpcaster {
    public bool CanUpcast(IEvent @event) => @event is OrderV2Event;
    public IEvent Upcast(IEvent @event) {
      var v2 = (OrderV2Event)@event;
      return new OrderV3Event { StreamId = v2.StreamId, Data = v2.Data };
    }
  }

  private sealed class RekeyUpcaster : IEventUpcaster {
    public static Guid KeyFor(Guid sagaId, string item) =>
      new(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{sagaId:N}:{item}"))[..16]);
    public bool CanUpcast(IEvent @event) => @event is RekeyableEvent;
    public IEvent Upcast(IEvent @event) {
      var e = (RekeyableEvent)@event;
      e.StreamId = KeyFor(e.SagaId, e.Item);
      return e;
    }
  }
}
