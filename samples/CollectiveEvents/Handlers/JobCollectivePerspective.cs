using System.Diagnostics.CodeAnalysis;
using CollectiveEvents.Sample.Events;
using CollectiveEvents.Sample.Models;
using Whizbang.Core.Perspectives;

namespace CollectiveEvents.Sample.Handlers;

/// <summary>
/// The sample perspective. Wires <see cref="JobModel"/> to the two
/// collective events with two
/// <see cref="CollectiveApplyForAttribute"/>-marked methods. The Slice 5
/// source generator discovers them at compile time, emits a static
/// dispatch table into the assembly (no reflection at runtime), and the
/// projection runner uses that table to route incoming collective events
/// to the right handler.
/// </summary>
/// <remarks>
/// <para>
/// Both handlers use <c>ScopeHandling = Framework</c> (the default), so
/// the framework wraps each spec's SET clause with:
/// </para>
/// <list type="number">
///   <item><description>The tenant scope filter from <c>TenantCollectiveScopeResolver</c> (<c>row.Scope.TenantId == scope.TenantId</c>).</description></item>
///   <item><description>The matched-id membership clause (<c>row.StreamId = ANY(MatchedStreamIds)</c>).</description></item>
/// </list>
/// <para>
/// Neither handler touches <c>ScopeContextAccessor</c> — they only
/// describe the mutation. That's what makes them safe by default: a
/// missing <c>WHERE</c> clause is impossible to write.
/// </para>
/// </remarks>
[SuppressMessage("Performance", "CA1822:Mark members as static",
  Justification = "Slice 5 generator emits ((HandlerType)handler).Method(evt) — handler dispatch requires instance methods even when the body does not touch instance state. Marking these static would break the generated registry.")]
public sealed class JobCollectivePerspective {

  /// <summary>
  /// Archive all jobs in the matched set. Constant-value SetProperty —
  /// the canonical "bulk state change" shape.
  /// </summary>
  [CollectiveApplyFor]
  public ICollectiveSpec<JobModel> ArchiveJobs(ArchiveJobsCollectiveEvent e) =>
    new CollectiveSpec<JobModel>(s => s
      .SetProperty(j => j.Status, "Archived")
      .SetProperty(j => j.ArchivedAt, e.OccurredAt));

  /// <summary>
  /// Increment view count and touch <c>LastViewedAt</c>. Computed-value
  /// SetProperty — shows the EF adapter handling
  /// <c>j =&gt; j.ViewCount + 1</c>. The Dapper adapter throws
  /// <c>NotSupportedException</c> on this shape in the first cut; use the
  /// raw-SQL escape hatch if you're on Dapper.
  /// </summary>
  [CollectiveApplyFor]
  public ICollectiveSpec<JobModel> BumpJobViewCount(BumpJobViewCountCollectiveEvent e) =>
    new CollectiveSpec<JobModel>(s => s
      .SetProperty(j => j.ViewCount, j => j.ViewCount + 1)
      .SetProperty(j => j.LastViewedAt, e.OccurredAt));
}
