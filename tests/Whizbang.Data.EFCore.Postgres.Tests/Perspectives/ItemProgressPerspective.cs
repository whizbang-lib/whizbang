// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A per-item progress perspective shaped like a fan-out's per-item rows: a Started event, then a Completed one,
/// each recorded in its own field, so the row shows whether each event was folded whatever order they were applied
/// in. Opted into <see cref="IVersionedApplyTarget"/>, the strict ordering per-item rows use. Exercised through its
/// generated runner in <see cref="PerspectiveLateEventApplyTests"/>.
/// </summary>
#pragma warning disable WHIZ105
public class ItemProgressPerspective :
    IPerspectiveFor<ItemProgress, ItemProgressStartedEvent>,
    IPerspectiveFor<ItemProgress, ItemProgressCompletedEvent> {
  public ItemProgressPerspective() { }

  public ItemProgress Apply(ItemProgress currentData, ItemProgressStartedEvent @event) =>
    new() { Id = @event.StreamId, Started = true, Attempts = currentData.Attempts + 1, Completed = currentData.Completed };

  public ItemProgress Apply(ItemProgress currentData, ItemProgressCompletedEvent @event) =>
    new() { Id = @event.StreamId, Started = currentData.Started, Attempts = currentData.Attempts, Completed = true };
}
#pragma warning restore WHIZ105

/// <summary>One item's progress: whether its Started and Completed events were folded, and how often Started was.</summary>
public class ItemProgress : IVersionedApplyTarget {
  [StreamId]
  public Guid Id { get; set; }
  public bool Started { get; set; }
  public int Attempts { get; set; }
  public bool Completed { get; set; }
}

public record ItemProgressStartedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
}

public record ItemProgressCompletedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
}
