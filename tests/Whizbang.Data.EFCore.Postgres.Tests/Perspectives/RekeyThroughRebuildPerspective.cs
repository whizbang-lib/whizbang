using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

#pragma warning disable WHIZ105

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Minimal perspective for the re-key-through-rebuild integration test
/// (<see cref="RekeyThroughRebuildTests"/>). Its single event type carries a
/// <see cref="RekeyTestEvent.TargetStreamId"/>; <see cref="RekeyUpcaster"/> moves the event's
/// <c>[StreamId]</c> onto that target on read. Rebuild must therefore project the event onto the
/// TARGET row, not the physical (stored) stream row — the behaviour the runner-seam fix enables.
/// </summary>
public class RekeyThroughRebuildPerspective : IPerspectiveFor<RekeyTestModel, RekeyTestEvent> {

  public RekeyThroughRebuildPerspective() { }

  public RekeyTestModel Apply(RekeyTestModel currentData, RekeyTestEvent @event) {
    return new RekeyTestModel {
      Id = @event.StreamId,
      Marker = @event.Marker
    };
  }
}

public class RekeyTestModel {
  [StreamId]
  public Guid Id { get; init; }
  public string Marker { get; init; } = string.Empty;
}

/// <summary>
/// Base event carrying the <c>[StreamId]</c> property by INHERITANCE — mirrors JDX's
/// <c>BaseSagaItemEvent</c>, where concrete saga-item events inherit their stream key. This is the
/// regression shape: the generator must walk the inheritance chain to find <c>[StreamId]</c>,
/// otherwise <c>ResolveTargetStreamId</c> can't route a re-keyed event onto its target row.
/// </summary>
public record RekeyTestEventBase {
  // Settable so the upcaster can re-key in place (mirrors BaseSagaItemEvent.StreamId in JDX).
  // The [StreamId] lives on the base; only the derived type implements IEvent, so the generator
  // must walk the inheritance chain to find it (the regression this test guards).
  [StreamId]
  public required Guid StreamId { get; set; }
}

public record RekeyTestEvent : RekeyTestEventBase, IEvent {
  public required string Marker { get; init; }

  /// <summary>When non-empty and different from <see cref="RekeyTestEventBase.StreamId"/>, the
  /// upcaster re-keys the event onto this stream. <see cref="Guid.Empty"/> means "stay on the
  /// physical stream".</summary>
  public Guid TargetStreamId { get; init; }
}

/// <summary>
/// Type-preserving re-key upcaster: moves a <see cref="RekeyTestEvent"/> onto its
/// <see cref="RekeyTestEvent.TargetStreamId"/>. Type-preserving on purpose — stream discovery
/// (<c>PerspectiveRebuilder._resolveStreamIdsToReplayAsync</c>) matches the STORED event type, so
/// keeping the type lets discovery find the physical stream while the row lands on the new stream.
/// </summary>
public sealed class RekeyUpcaster : IEventUpcaster {
  public bool CanUpcast(IEvent storedEvent) =>
      storedEvent is RekeyTestEvent e && e.TargetStreamId != Guid.Empty && e.StreamId != e.TargetStreamId;

  public IEvent Upcast(IEvent storedEvent) {
    var e = (RekeyTestEvent)storedEvent;
    e.StreamId = e.TargetStreamId;
    return e;
  }
}
