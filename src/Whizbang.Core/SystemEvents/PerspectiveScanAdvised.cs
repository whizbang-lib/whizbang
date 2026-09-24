using Whizbang.Core.Attributes;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// A perspective whose reads are measurably being answered by scanning its table.
/// </summary>
/// <remarks>
/// The measured counterpart to <see cref="PerspectiveIndexAdvised"/>. That one reads the model and
/// says a filter has no index behind it; this one reads the counters and says the filter has been
/// expensive. A perspective can raise the first for a year without the second ever being true,
/// which is the difference the threshold exists to keep.
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/PerspectiveScanAdvisoryTests.cs</tests>
[AuditEvent(Exclude = true, Reason = "System event - an advisory about the store is not itself audited")]
[PinnedId("0769ce19-f5dc-4bbf-9bb2-c0867d25d940")]
public sealed record PerspectiveScanAdvised : ISystemEvent {
  /// <summary>Unique identifier for this event.</summary>
  [StreamId]
  public Guid Id { get; init; } = TrackedGuid.NewMedo();

  /// <summary>The perspective table the finding is about.</summary>
  public required string TableName { get; init; }

  /// <summary>Live rows returned by sequential scan, which is what the scanning cost.</summary>
  public required long SequentialRowsRead { get; init; }

  /// <summary>Sequential scans started.</summary>
  public required long SequentialScans { get; init; }

  /// <summary>Index scans started, which the sequential ones are weighed against.</summary>
  public required long IndexScans { get; init; }

  /// <summary>The table's size in bytes when the finding was made.</summary>
  public required long TableSizeBytes { get; init; }

  /// <summary>The row count the finding was raised against.</summary>
  public required long RowsThreshold { get; init; }
}
