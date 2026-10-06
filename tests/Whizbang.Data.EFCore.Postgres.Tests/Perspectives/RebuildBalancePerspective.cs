// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
    IPerspectiveFor<RebuildBalanceModel, RebuildDebitedEvent>,
    IPerspectiveWithActionsFor<RebuildBalanceModel, RebuildClosedEvent> {

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

  // A closed account leaves no row. Mirrors a real projection that purges on a terminal event -- the
  // shape that 32 ended sessions in an upgraded environment sat in, still present, after a rebuild.
  public ApplyResult<RebuildBalanceModel> Apply(RebuildBalanceModel currentData, RebuildClosedEvent @event) {
    return ApplyResult<RebuildBalanceModel>.Purge();
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

public record RebuildClosedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
}
