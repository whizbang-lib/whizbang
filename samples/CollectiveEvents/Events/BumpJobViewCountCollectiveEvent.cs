using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace CollectiveEvents.Sample.Events;

/// <summary>
/// Collective event: increment <see cref="Models.JobModel.ViewCount"/>
/// by one and touch <see cref="Models.JobModel.LastViewedAt"/> on every
/// matched stream. Demonstrates the
/// <c>SetProperty(selector, computed)</c> shape — the value depends on
/// the row's own current state (<c>j =&gt; j.ViewCount + 1</c>), not a
/// constant.
/// </summary>
/// <remarks>
/// The EF adapter translates the computed expression into a
/// <c>row.Data.ViewCount + 1</c>-equivalent jsonb mutation; the Dapper
/// driver throws <see cref="NotSupportedException"/> on this shape in
/// the first cut (escape hatch: <c>SpecKind = RawSql</c>). The sample's
/// handler still demonstrates the surface — picking which driver to use
/// is a perspective-level configuration decision.
/// </remarks>
[PinnedId("7e1c5a3f-8b9d-4e2c-bf60-3d5a8c1e9b42")]
public sealed record BumpJobViewCountCollectiveEvent : ICollectiveEvent {
  public required ICollectiveScope Scope { get; init; }
  public required IReadOnlyList<Guid> MatchedStreamIds { get; init; }
  public required DateTimeOffset OccurredAt { get; init; }
}
