// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A perspective whose rows the tests register as evictable (a row cap), and no other test does: a missing row of it
/// is ambiguous, a new stream or an evicted row whose stream woke, which the runner settles by probing the stream's
/// history. Exercised in <see cref="PerspectivePrepareCompleteIntegrationTests"/>.
/// </summary>
public class PreparedApplyCappedPerspective : IPerspectiveFor<PreparedApplyCappedItem, PreparedApplyCappedItemAddedEvent> {
  public PreparedApplyCappedItem Apply(PreparedApplyCappedItem currentData, PreparedApplyCappedItemAddedEvent @event) =>
    new() { Id = @event.StreamId, Added = currentData.Added + 1 };
}

/// <summary>How many items were added to the row: a row rebuilt from its whole stream counts every one.</summary>
public class PreparedApplyCappedItem {
  [StreamId]
  public Guid Id { get; set; }
  public int Added { get; set; }
}

public record PreparedApplyCappedItemAddedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
}
