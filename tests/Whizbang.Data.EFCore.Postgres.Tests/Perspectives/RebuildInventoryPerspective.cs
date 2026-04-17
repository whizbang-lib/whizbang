using Whizbang.Core;
using Whizbang.Core.Perspectives;

#pragma warning disable WHIZ105

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Second test perspective used by PerspectiveRebuilderIntegrationTests to prove that a
/// targeted rebuild of one perspective does not touch another perspective's projection.
/// Keeps a simple running count of StockAdjusted events.
/// </summary>
public class RebuildInventoryPerspective :
    IPerspectiveFor<RebuildInventoryModel, RebuildStockAdjustedEvent> {

  public RebuildInventoryPerspective() { }

  public RebuildInventoryModel Apply(RebuildInventoryModel currentData, RebuildStockAdjustedEvent @event) {
    return new RebuildInventoryModel {
      Id = @event.StreamId,
      OnHand = currentData.OnHand + @event.Delta
    };
  }
}

public class RebuildInventoryModel {
  [StreamId]
  public Guid Id { get; init; }
  public int OnHand { get; init; }
}

public record RebuildStockAdjustedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required int Delta { get; init; }
}
