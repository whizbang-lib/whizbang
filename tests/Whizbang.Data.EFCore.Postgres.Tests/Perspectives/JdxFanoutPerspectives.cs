using Whizbang.Core;
using Whizbang.Core.Perspectives;

#pragma warning disable WHIZ105

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Reproduction perspectives for the JDX 2026-05-03 UI loader bug.
/// One event (<see cref="JdxFanoutEvent"/>) fans out to TWO perspectives on the
/// SAME stream: a scalar model (mirrors DraftJobName) and a list-property model
/// (mirrors DraftJobWorkingConditions). The bug was that only the scalar
/// perspective's row appeared in Postgres after the event. These perspectives
/// + their generated runners power
/// <c>MultiPerspectiveJdxFanoutEndToEndTests</c>.
/// </summary>
public record JdxFanoutEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Title { get; init; }
  public required List<string> ConditionLabels { get; init; }
}

/// <summary>Scalar model — mirrors DraftJobName.</summary>
public class JdxScalarModel {
  [StreamId]
  public Guid Id { get; init; }
  public string Title { get; init; } = "";
}

/// <summary>List-property model — mirrors DraftJobWorkingConditions, the JDX-failing one.</summary>
public class JdxListModel {
  [StreamId]
  public Guid Id { get; init; }
  public string Title { get; init; } = "";
  public List<JdxConditionRow> Conditions { get; init; } = [];
}

public class JdxConditionRow {
  public string Label { get; init; } = "";
  public int Order { get; init; }
}

/// <summary>Scalar perspective — applies JdxFanoutEvent into JdxScalarModel.</summary>
public class JdxScalarPerspective : IPerspectiveFor<JdxScalarModel, JdxFanoutEvent> {
  public JdxScalarPerspective() { }

  public JdxScalarModel Apply(JdxScalarModel currentData, JdxFanoutEvent @event) {
    return new JdxScalarModel {
      Id = @event.StreamId,
      Title = @event.Title
    };
  }
}

/// <summary>List perspective — applies JdxFanoutEvent into JdxListModel with a populated Conditions list.</summary>
public class JdxListPerspective : IPerspectiveFor<JdxListModel, JdxFanoutEvent> {
  public JdxListPerspective() { }

  public JdxListModel Apply(JdxListModel currentData, JdxFanoutEvent @event) {
    var rows = new List<JdxConditionRow>(@event.ConditionLabels.Count);
    for (var i = 0; i < @event.ConditionLabels.Count; i++) {
      rows.Add(new JdxConditionRow { Label = @event.ConditionLabels[i], Order = i });
    }
    return new JdxListModel {
      Id = @event.StreamId,
      Title = @event.Title,
      Conditions = rows
    };
  }
}
