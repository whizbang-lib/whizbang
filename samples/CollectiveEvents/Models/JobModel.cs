using Whizbang.Core.Perspectives;

namespace CollectiveEvents.Sample.Models;

/// <summary>
/// Simple perspective model for the collective-events sample. Stands in for
/// a consumer's <c>JobModel</c> — uniform mutation ("archive everyone matching a
/// tenant scope") is the canonical use case for an
/// <c>ICollectiveEvent</c>.
/// </summary>
/// <remarks>
/// <para>
/// In a real service, this model would live in the consuming project's
/// contracts assembly and be projected by a perspective registered with
/// the Whizbang runner. For this sample we just want the user-facing
/// surface area visible — handler signatures, attribute usage, the spec
/// shape — so the model only exposes the columns the sample mutates.
/// </para>
/// <para>
/// <c>Status</c> is <c>[Indexed]</c> because <c>ArchiveJobs</c> filters its cohort on it. A collective
/// predicate reads the field as an extraction from the stored document, which only the field's own
/// index answers; without it, every apply reads every row (WHIZ309).
/// </para>
/// </remarks>
public sealed record JobModel {
  [Indexed]
  public required string Status { get; init; }
  public int ViewCount { get; init; }
  public DateTimeOffset? ArchivedAt { get; init; }
  public DateTimeOffset? LastViewedAt { get; init; }
}
