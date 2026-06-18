namespace CollectiveEvents.Sample.Models;

/// <summary>
/// Simple perspective model for the collective-events sample. Stands in for
/// JDX's <c>JobModel</c> — uniform mutation ("archive everyone matching a
/// tenant scope") is the canonical use case for an
/// <c>ICollectiveEvent</c>.
/// </summary>
/// <remarks>
/// In a real service, this model would live in the consuming project's
/// contracts assembly and be projected by a perspective registered with
/// the Whizbang runner. For this sample we just want the user-facing
/// surface area visible — handler signatures, attribute usage, the spec
/// shape — so the model only exposes the columns the sample mutates.
/// </remarks>
public sealed record JobModel {
  public required string Status { get; init; }
  public int ViewCount { get; init; }
  public DateTimeOffset? ArchivedAt { get; init; }
  public DateTimeOffset? LastViewedAt { get; init; }
}
