// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
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
    IPerspectiveFor<RebuildBalanceModel, RebuildNonCreatingEvent>,
    IPerspectiveFor<RebuildBalanceModel, RebuildBalanceSetEvent>,
    IPerspectiveFor<RebuildBalanceModel, RebuildLabelSetEvent>,
    IPerspectiveFor<RebuildBalanceModel, RebuildBalanceRestatedEvent>,
    IPerspectiveWithActionsFor<RebuildBalanceModel, RebuildClosedEvent> {

  public RebuildBalancePerspective() { }

  // Counted per stream rather than in one process-wide total: the tests run in parallel, and another test folding a
  // credit into its own stream must not move the count a test reads for its stream (#2069).
  private static readonly ConcurrentDictionary<Guid, int> _creditsApplied = new();

  /// <summary>How many credits were folded into <paramref name="streamId"/>: the evidence a replay was skipped.</summary>
  public static int CreditsAppliedTo(Guid streamId) => _creditsApplied.GetValueOrDefault(streamId);

  public RebuildBalanceModel Apply(RebuildBalanceModel currentData, RebuildCreditedEvent @event) {
    _creditsApplied.AddOrUpdate(@event.StreamId, 1, static (_, count) => count + 1);
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

  // "Leave the stream without a row": a null model with no action, which is the documented way a
  // projection declines to create a row from an event that is not a creating one. A real projection
  // reaches for it on the events that may legitimately arrive before the row exists -- a version bump
  // that is not the initial draft, an update to a job that was since deleted -- and relies on a LATER
  // creating event to bring the row into being.
  public RebuildBalanceModel Apply(RebuildBalanceModel currentData, RebuildNonCreatingEvent @event) {
    return null!;
  }

  // The last write wins: the fold's result depends on the order it is applied in, which a sum does not (#1352).
  public RebuildBalanceModel Apply(RebuildBalanceModel currentData, RebuildBalanceSetEvent @event) {
    return new RebuildBalanceModel { Id = @event.StreamId, Balance = @event.Balance, Label = currentData.Label };
  }

  // A second last-write-wins field, so a test can compare a whole row and not only the field it expected to change.
  public RebuildBalanceModel Apply(RebuildBalanceModel currentData, RebuildLabelSetEvent @event) {
    return new RebuildBalanceModel { Id = @event.StreamId, Balance = currentData.Balance, Label = @event.Label };
  }

  // A superseding restatement (#2152): the newest of each account key carries the whole balance, so the reaper can remove
  // the older ones, the stream's first event among them.
  public RebuildBalanceModel Apply(RebuildBalanceModel currentData, RebuildBalanceRestatedEvent @event) {
    return new RebuildBalanceModel { Id = @event.StreamId, Balance = @event.Balance, Label = currentData.Label };
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
  public string? Label { get; init; }
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

/// <summary>An event the perspective folds but which never creates a row.</summary>
public record RebuildNonCreatingEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
}

/// <summary>Sets the balance outright, so the order a replay folds in decides the result.</summary>
public record RebuildBalanceSetEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required decimal Balance { get; init; }
}

/// <summary>Sets the label outright: a second field whose final value depends on fold order.</summary>
public record RebuildLabelSetEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Label { get; init; }
}

/// <summary>Restates the balance in full: a superseding event, so an older one of the same key is reaped (#2152).</summary>
[Whizbang.Core.Attributes.Superseding]
public record RebuildBalanceRestatedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  [Whizbang.Core.Attributes.SupersedingKey]
  public required string Account { get; init; }
  public required decimal Balance { get; init; }
}

public record RebuildClosedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
}
