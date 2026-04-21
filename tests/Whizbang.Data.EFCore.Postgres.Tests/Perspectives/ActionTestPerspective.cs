using Whizbang.Core;
using Whizbang.Core.Perspectives;

// Suppress WHIZ105 in test files - we intentionally inject impure services for testing
#pragma warning disable WHIZ105

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Test perspective demonstrating all 4 ModelAction outcomes: None (update), Delete, and Purge.
/// Uses both IPerspectiveFor (for simple updates) and IPerspectiveWithActionsFor (for delete/purge).
/// </summary>
public class ActionTestPerspective :
    IPerspectiveFor<ActionTestModel, ActionTestCreatedEvent>,
    IPerspectiveFor<ActionTestModel, ActionTestUpdatedEvent>,
    IPerspectiveWithActionsFor<ActionTestModel, ActionTestSoftDeletedEvent>,
    IPerspectiveWithActionsFor<ActionTestModel, ActionTestPurgedEvent>,
    IPerspectiveWithActionsFor<ActionTestModel, ActionTestIgnoredEvent> {

  /// <summary>
  /// Parameterless constructor for generator-based Apply calls.
  /// </summary>
  public ActionTestPerspective() { }

  public ActionTestModel Apply(ActionTestModel currentData, ActionTestCreatedEvent @event) {
    return new ActionTestModel {
      Id = @event.StreamId,
      Name = @event.Name,
      Value = @event.Value,
      DeletedAt = null
    };
  }

  public ActionTestModel Apply(ActionTestModel currentData, ActionTestUpdatedEvent @event) {
    return new ActionTestModel {
      Id = currentData.Id,
      Name = currentData.Name,
      Value = @event.NewValue,
      DeletedAt = currentData.DeletedAt
    };
  }

  public ApplyResult<ActionTestModel> Apply(ActionTestModel currentData, ActionTestSoftDeletedEvent @event) {
    var updated = new ActionTestModel {
      Id = currentData.Id,
      Name = currentData.Name,
      Value = currentData.Value,
      DeletedAt = @event.DeletedAt
    };
    return new ApplyResult<ActionTestModel>(updated, ModelAction.Delete);
  }

  public ApplyResult<ActionTestModel> Apply(ActionTestModel currentData, ActionTestPurgedEvent @event) {
    return ApplyResult<ActionTestModel>.Purge();
  }

  public ApplyResult<ActionTestModel> Apply(ActionTestModel currentData, ActionTestIgnoredEvent @event) {
    return ApplyResult<ActionTestModel>.None();
  }
}

/// <summary>
/// Read model maintained by ActionTestPerspective.
/// </summary>
public class ActionTestModel {
  [StreamId]
  public Guid Id { get; init; }
  public string Name { get; init; } = "";
  public int Value { get; init; }
  public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>
/// Event that creates a new ActionTestModel.
/// </summary>
public record ActionTestCreatedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Name { get; init; }
  public required int Value { get; init; }
}

/// <summary>
/// Event that updates the Value on an existing ActionTestModel.
/// </summary>
public record ActionTestUpdatedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required int NewValue { get; init; }
}

/// <summary>
/// Event that triggers a soft delete (sets DeletedAt, keeps the row).
/// </summary>
public record ActionTestSoftDeletedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required DateTimeOffset DeletedAt { get; init; }
}

/// <summary>
/// Event that triggers a hard delete (removes the row entirely).
/// </summary>
public record ActionTestPurgedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
}

/// <summary>
/// Event the perspective opts out of via <see cref="ApplyResult{TModel}.None"/>.
/// No write should occur — on a new stream no phantom default row should be inserted;
/// on an existing stream the row should be left unchanged.
/// </summary>
public record ActionTestIgnoredEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
}
