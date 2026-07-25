using Whizbang.Core;
using Whizbang.Core.Perspectives;

#pragma warning disable WHIZ105

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Reproduction perspectives for a consumer's UI loader bug, observed in production.
/// One event (<see cref="ConsumerFanoutEvent"/>) fans out to TWO perspectives on the
/// SAME stream: a scalar model (mirrors OrderModel) and a list-property model
/// (mirrors OrderLines). The bug was that only the scalar
/// perspective's row appeared in Postgres after the event. These perspectives
/// + their generated runners power
/// <c>MultiPerspectiveConsumerFanoutEndToEndTests</c>.
/// </summary>
public record ConsumerFanoutEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Title { get; init; }
  public required List<string> ConditionLabels { get; init; }
}

/// <summary>Scalar model — mirrors OrderModel.</summary>
public class ConsumerScalarModel {
  [StreamId]
  public Guid Id { get; init; }
  public string Title { get; init; } = "";
}

/// <summary>List-property model — mirrors OrderLines, the failing one in production.</summary>
public class ConsumerListModel {
  [StreamId]
  public Guid Id { get; init; }
  public string Title { get; init; } = "";
  public List<ConsumerConditionRow> Conditions { get; init; } = [];
}

public class ConsumerConditionRow {
  public string Label { get; init; } = "";
  public int Order { get; init; }
}

/// <summary>Scalar perspective — applies ConsumerFanoutEvent into ConsumerScalarModel.</summary>
public class ConsumerScalarPerspective : IPerspectiveFor<ConsumerScalarModel, ConsumerFanoutEvent> {
  public ConsumerScalarPerspective() { }

  public ConsumerScalarModel Apply(ConsumerScalarModel currentData, ConsumerFanoutEvent @event) {
    return new ConsumerScalarModel {
      Id = @event.StreamId,
      Title = @event.Title
    };
  }
}

/// <summary>List perspective — applies ConsumerFanoutEvent into ConsumerListModel with a populated Conditions list.</summary>
public class ConsumerListPerspective : IPerspectiveFor<ConsumerListModel, ConsumerFanoutEvent> {
  public ConsumerListPerspective() { }

  public ConsumerListModel Apply(ConsumerListModel currentData, ConsumerFanoutEvent @event) {
    var rows = new List<ConsumerConditionRow>(@event.ConditionLabels.Count);
    for (var i = 0; i < @event.ConditionLabels.Count; i++) {
      rows.Add(new ConsumerConditionRow { Label = @event.ConditionLabels[i], Order = i });
    }
    return new ConsumerListModel {
      Id = @event.StreamId,
      Title = @event.Title,
      Conditions = rows
    };
  }
}
