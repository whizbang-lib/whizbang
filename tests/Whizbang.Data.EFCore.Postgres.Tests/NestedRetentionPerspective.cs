using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

public record NestedRetentionEvent : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public int Delta { get; init; }
}

/// <summary>
/// A NESTED model class: the shape whose row-retention declaration never reached the registry
/// (issue #697). The generator wrote the registry key as <c>Owner.Model</c>; the runtime looked it
/// up as <c>Owner+Model</c>.
/// </summary>
public static class NestedRetentionOwner {
  public class Model {
    [StreamId]
    public Guid Id { get; set; }
    public int Total { get; set; }
  }
}

[RowTtl(Days = 1)]
public class NestedRetentionPerspective : IPerspectiveFor<NestedRetentionOwner.Model, NestedRetentionEvent> {
  public NestedRetentionOwner.Model Apply(NestedRetentionOwner.Model currentData, NestedRetentionEvent @event) {
    currentData.Total += @event.Delta;
    return currentData;
  }
}
