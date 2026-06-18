using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace CollectiveEvents.Sample.Events;

/// <summary>
/// Collective event: archive every <see cref="Models.JobModel"/> row
/// in the scope <see cref="Scope"/> carries. One event row, one SQL
/// UPDATE — replaces the per-row cost shape of emitting N
/// <c>JobArchivedEvent</c>s when the producer expresses "archive the
/// whole tenant" rather than handpicked jobs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope IS the descriptor.</strong> The event carries no
/// enumerated set of stream ids — it carries only its scope. At apply
/// time the projection runner composes <c>WHERE</c> from the resolver's
/// <c>ScopeFilter(Scope)</c> alone; every row currently in scope gets
/// archived.
/// </para>
/// <para>
/// <strong>Replay determinism.</strong> Because event-sourcing
/// guarantees the projection state at any point in the event sequence
/// is fully determined by the events up to that point, the predicate
/// evaluates the same set on every replay — and that set reflects the
/// logically correct projection state, not the original execution's
/// possibly-wrong (e.g. out-of-order delivery) result.
/// </para>
/// </remarks>
[PinnedId("2c4e9f8d-1a3b-4c5e-9d7f-0e6a8b2d4f51")]
public sealed record ArchiveJobsCollectiveEvent : ICollectiveEvent {
  public required ICollectiveScope Scope { get; init; }
  public required DateTimeOffset OccurredAt { get; init; }
}
