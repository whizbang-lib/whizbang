using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Trivial perspective so the source generator registers
/// <see cref="PerspectiveRow{VersionedItem}"/> with the test <c>WorkCoordinationDbContext</c>.
/// Behavior is exercised directly via <see cref="PostgresUpsertStrategy"/> in
/// <see cref="VersionedApplyTargetTests"/> — this Apply is only here to satisfy registration.
/// </summary>
#pragma warning disable WHIZ105
public class VersionedItemPerspective : IPerspectiveFor<VersionedItem, VersionedItemCreatedEvent> {
  public VersionedItemPerspective() { }
  public VersionedItem Apply(VersionedItem currentData, VersionedItemCreatedEvent @event) =>
    new() { Id = @event.StreamId, State = @event.State, Value = @event.Value };
}
#pragma warning restore WHIZ105

/// <summary>
/// Read model opted into strict cross-pod ordering via <see cref="IVersionedApplyTarget"/>.
/// Mirrors <see cref="Whizbang.Sagas.Models.SagaItemModel"/>'s opt-in for the cross-pod
/// strand-prevention contract.
/// </summary>
public class VersionedItem : IVersionedApplyTarget {
  [StreamId]
  public Guid Id { get; set; }
  public string State { get; set; } = string.Empty;
  public int Value { get; set; }
}

public record VersionedItemCreatedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string State { get; init; }
  public required int Value { get; init; }
}
