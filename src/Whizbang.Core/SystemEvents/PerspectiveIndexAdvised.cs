using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Lenses;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Emitted when a perspective has grown large enough that a query shape it exposes, with no index
/// behind it, costs real time.
/// </summary>
/// <remarks>
/// <para>
/// The same finding is logged. It is emitted as an event as well because a log line is the end of
/// the road: the host cannot route it, and the framework should not decide what an advisory
/// becomes. One deployment raises a work item from this, another only records it, and a third
/// ignores it because the table is about to be archived. Those are not the framework's calls.
/// </para>
/// <para>
/// The build-time analyzer raises the same concern and cannot finish the argument, because it has
/// no idea how big the table is. This one knows, which is why it is worth interrupting an operator
/// for and the build warning often is not.
/// </para>
/// <para>
/// Advisory only. Nothing acts on it. Creating a column and an index changes write cost, storage
/// and lock behaviour, and on a large table the build itself is an event -- so the decision stays
/// with the author, and this exists to put it in front of them.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#perspective-index-advised</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/QueryExposureAdvisoryTests.cs</tests>
[AuditEvent(Exclude = true, Reason = "System event - an advisory about the store is not itself audited")]
[PinnedId("f4b31d16-06eb-4b0c-a152-d014db86286f")]
public sealed record PerspectiveIndexAdvised : ISystemEvent {
  /// <summary>Unique identifier for this event.</summary>
  [StreamId]
  public Guid Id { get; init; } = TrackedGuid.NewMedo();

  /// <summary>The perspective model the finding is about.</summary>
  public required string ModelName { get; init; }

  /// <summary>The physical table behind that model.</summary>
  public required string TableName { get; init; }

  /// <summary>
  /// The table's size in bytes, as the catalog reported it when the finding was made.
  /// </summary>
  /// <remarks>
  /// Size rather than a row count because that is what the catalog gives without a scan, and it is
  /// the better measure anyway: what an unindexed read costs is the bytes it has to get through.
  /// </remarks>
  public required long TableSizeBytes { get; init; }

  /// <summary>The size the finding was raised against, so a reader can see how far past it this is.</summary>
  public required long ThresholdBytes { get; init; }

  /// <summary>How the query shape reaches this model: what a request is able to ask of it.</summary>
  public required string Exposure { get; init; }

  /// <summary>
  /// The exposed fields with no index behind them, which is what an author would act on.
  /// </summary>
  /// <remarks>
  /// The fields, not a count: the point of the advisory is to turn "look at this perspective" into
  /// "promote this property", and a count cannot do that.
  /// </remarks>
  public required IReadOnlyList<string> UnindexedFields { get; init; }
}
