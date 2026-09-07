using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers <see cref="EventUpcasterPipeline.ExtraInputTypeNamesFor"/>'s "target not requested" skip —
/// the sibling <c>EventUpcasterPipelineTests</c> only exercises the matching (overlap) case for this
/// name-based overload; the equivalent non-match case is covered only for its
/// <see cref="EventUpcasterPipeline.ExtraInputTypesFor"/> <see cref="Type"/>-based twin.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EventUpcasterPipeline.cs</code-under-test>
public class EventUpcasterPipelineCoverageTests {
#pragma warning disable WHIZ009
  public record OrderV1Event : IEvent {
    [StreamId] public Guid StreamId { get; set; }
  }

  public record OrderV2Event : IEvent {
    [StreamId] public Guid StreamId { get; set; }
  }

  public record OrderV3Event : IEvent {
    [StreamId] public Guid StreamId { get; set; }
  }
#pragma warning restore WHIZ009

  private sealed class _v1ToV2Upcaster : IEventUpcaster {
    public IReadOnlyList<Type> SourceTypes => [typeof(OrderV1Event)];
    public IReadOnlyList<Type> TargetTypes => [typeof(OrderV2Event)];
    public bool CanUpcast(IEvent @event) => @event is OrderV1Event;
    public IEvent Upcast(IEvent @event) => new OrderV2Event { StreamId = ((OrderV1Event)@event).StreamId };
  }

  [Test]
  public async Task ExtraInputTypeNamesFor_TargetNotRequested_ReturnsEmptyAsync() {
    // If the "no overlap -> continue" branch regressed to always widening, every rebuild/read-seam
    // scope would pull in this upcaster's source type even for callers who never asked for its
    // target — silently widening every unrelated perspective's stream scan.
    var pipeline = new EventUpcasterPipeline([new _v1ToV2Upcaster()]);

    var names = pipeline.ExtraInputTypeNamesFor([TypeNameFormatter.Format(typeof(OrderV3Event))]);

    await Assert.That(names.Count).IsEqualTo(0);
  }
}
