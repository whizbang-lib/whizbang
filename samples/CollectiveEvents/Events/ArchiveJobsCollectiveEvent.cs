using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace CollectiveEvents.Sample.Events;

/// <summary>
/// Collective event: archive every job whose <see cref="Models.JobModel"/>
/// row is in <see cref="MatchedStreamIds"/> for the tenant in
/// <see cref="Scope"/>. One event row, one SQL UPDATE — replaces the
/// per-row cost shape of emitting N <c>JobArchivedEvent</c>s when the
/// producer expresses "archive the whole tenant" rather than handpicked
/// jobs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Snapshot determinism.</strong> The producer evaluates the
/// predicate ("all non-archived jobs for tenant T") at write time and
/// freezes the result into <see cref="MatchedStreamIds"/>. Replay
/// re-applies against that captured set — jobs created after the event
/// was written are NOT retroactively archived, even if they would have
/// matched the original predicate. That's the locked invariant.
/// </para>
/// <para>
/// <strong>Scope vs matched set.</strong> The two are belt-and-braces:
/// the <see cref="TenantCollectiveScope"/> tells the resolver "only
/// touch rows in tenant T" (composed as an outer WHERE), and the
/// <see cref="MatchedStreamIds"/> set narrows further to the specific
/// stream ids in scope at write time. Both are enforced — a row must
/// pass both to be mutated.
/// </para>
/// </remarks>
[PinnedId("2c4e9f8d-1a3b-4c5e-9d7f-0e6a8b2d4f51")]
public sealed record ArchiveJobsCollectiveEvent : ICollectiveEvent {
  public required ICollectiveScope Scope { get; init; }
  public required IReadOnlyList<Guid> MatchedStreamIds { get; init; }
  public required DateTimeOffset OccurredAt { get; init; }
}
