using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End-to-end regression for the jsonb-vs-STJ polymorphic-discriminator defect, through a REAL
/// PostgreSQL jsonb column (Testcontainers) and the REAL generator-produced polymorphic factory —
/// not a simulator. An event carries a nested abstract <c>[JsonPolymorphic]</c> DTO whose concrete
/// type has a 1-char direct property (<c>A</c>); the writer emits <c>$type</c> first, but the jsonb
/// column reorders keys so <c>A</c> sorts ahead of <c>$type</c>. Without
/// <c>AllowOutOfOrderMetadataProperties</c> (set by <c>JsonContextRegistry.CreateCombinedOptions</c>),
/// the read throws <c>NotSupportedException</c>; with it, the DTO round-trips.
/// A string round-trip cannot reproduce this — only a real jsonb column reorders keys.
/// </summary>
[Category("Shard2")]
public class JsonbPolymorphicRoundTripIntegrationTests : EFCoreTestBase {
  [Test]
  public async Task ReadAsync_EventWithNestedPolymorphicShortKeyDto_RoundTripsThroughRealJsonbAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var payload = new PictureEvent {
      PictureId = streamId,
      Shape3d = new ShapeObjectDto { A = 680, Id = "seed-fanfare-2", Shape = "Rect" }
    };
    var envelope = new MessageEnvelope<PictureEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTime.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "test-service",
            HostName = "test-host",
            ProcessId = 123
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act — append (writes $type-first JSON into the jsonb column, which reorders keys) then read back.
    await eventStore.AppendAsync(streamId, envelope);

    var events = new List<MessageEnvelope<PictureEvent>>();
    await foreach (var evt in eventStore.ReadAsync<PictureEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }

    // Assert — the nested polymorphic DTO survived the jsonb round-trip as its concrete type.
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Payload.Shape3d).IsTypeOf<ShapeObjectDto>();
    var shape = (ShapeObjectDto)events[0].Payload.Shape3d;
    await Assert.That(shape.A).IsEqualTo(680);
    await Assert.That(shape.Id).IsEqualTo("seed-fanfare-2");
    await Assert.That(shape.Shape).IsEqualTo("Rect");
  }
}

/// <summary>Event carrying a nested polymorphic DTO — the generator discovers <see cref="PictureObjectDto"/>
/// (abstract, <c>[JsonPolymorphic]</c>) as a reachable property type and emits its polymorphic factory.</summary>
public record PictureEvent : IEvent {
  [StreamId]
  public required Guid PictureId { get; init; }
  public required PictureObjectDto Shape3d { get; init; }
}

/// <summary>Abstract polymorphic base stored inside <see cref="PictureEvent"/>. Whizbang manages the
/// wire format ($type + CLR simple name); the attributes only drive derived-type discovery.</summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(ShapeObjectDto), nameof(ShapeObjectDto))]
public abstract record PictureObjectDto;

/// <summary>Concrete leaf with a 1-char direct property (<c>A</c>) — the ingredient that pushes
/// <c>$type</c> out of first position after Postgres jsonb key normalization.</summary>
public record ShapeObjectDto : PictureObjectDto {
  public required int A { get; init; }
  public string? Id { get; init; }
  public required string Shape { get; init; }
}
