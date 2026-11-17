// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A per-item perspective whose row records the order its events were folded in (#2379): each Started appends "S" and
/// sets the state Running, each Completed appends "C" and sets it Completed. A row folded in the producer's order
/// (three Started, then Completed) reads "SSSC" and Completed; any other order reads differently, so the row says which
/// order the framework folded. Opted into <see cref="IVersionedApplyTarget"/>, as per-item rows are.
/// </summary>
#pragma warning disable WHIZ105
public class ItemTrailPerspective :
    IPerspectiveFor<ItemTrail, ItemProgressStartedEvent>,
    IPerspectiveFor<ItemTrail, ItemProgressCompletedEvent> {
  public ItemTrailPerspective() { }

  public ItemTrail Apply(ItemTrail currentData, ItemProgressStartedEvent @event) =>
    new() { Id = @event.StreamId, State = "Running", Trail = currentData.Trail + "S" };

  public ItemTrail Apply(ItemTrail currentData, ItemProgressCompletedEvent @event) =>
    new() { Id = @event.StreamId, State = "Completed", Trail = currentData.Trail + "C" };
}
#pragma warning restore WHIZ105

/// <summary>One item's state and the order its events were folded in.</summary>
public class ItemTrail : IVersionedApplyTarget {
  [StreamId]
  public Guid Id { get; set; }
  public string State { get; set; } = string.Empty;
  public string Trail { get; set; } = string.Empty;
}
