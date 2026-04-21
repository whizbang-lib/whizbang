using Whizbang.Core;
using Whizbang.Core.Perspectives;

#pragma warning disable WHIZ105

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Test perspective used by PerspectiveRebuilderIntegrationTests.
/// Maintains a running decimal balance on a stream so integration tests can assert
/// that replay produces the correct final value.
/// </summary>
public class RebuildBalancePerspective :
    IPerspectiveFor<RebuildBalanceModel, RebuildCreditedEvent>,
    IPerspectiveFor<RebuildBalanceModel, RebuildDebitedEvent> {

  public RebuildBalancePerspective() { }

  public RebuildBalanceModel Apply(RebuildBalanceModel currentData, RebuildCreditedEvent @event) {
    return new RebuildBalanceModel {
      Id = @event.StreamId,
      Balance = currentData.Balance + @event.Amount
    };
  }

  public RebuildBalanceModel Apply(RebuildBalanceModel currentData, RebuildDebitedEvent @event) {
    return new RebuildBalanceModel {
      Id = currentData.Id == Guid.Empty ? @event.StreamId : currentData.Id,
      Balance = currentData.Balance - @event.Amount
    };
  }
}

public class RebuildBalanceModel {
  [StreamId]
  public Guid Id { get; init; }
  public decimal Balance { get; init; }
}

public record RebuildCreditedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required decimal Amount { get; init; }
}

public record RebuildDebitedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required decimal Amount { get; init; }
}
