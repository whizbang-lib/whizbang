// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Observability;

/// <summary>What one pending-reclamation reading asks the provider for (#2289).</summary>
/// <param name="ClaimKey">
/// The window's claim: one instance of the fleet takes it and reads, so a dashboard summing across instances does not
/// count the store twice.
/// </param>
/// <param name="CountCap">The most rows one task's count reads; a count that reaches it is reported as at least that.</param>
/// <docs>operations/observability/metrics#pending-reclamation</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Observability/PendingReclamationSamplerTests.cs</tests>
public readonly record struct PendingReclamationRequest(string ClaimKey, int CountCap);

/// <summary>Rows a cleanup task would remove now and has not, from a bounded count (#2289).</summary>
/// <param name="Rows">The rows counted, at most the cap.</param>
/// <param name="Capped">Whether the count stopped at the cap: there are at least <paramref name="Rows"/>.</param>
/// <docs>operations/observability/metrics#pending-reclamation</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MaintenanceMetricsPendingTests.cs</tests>
public readonly record struct PendingRows(long Rows, bool Capped);

/// <summary>How much storage cleanup has left to do (#2289).</summary>
/// <param name="Tasks">
/// Per maintenance task (the <c>task</c> tag of <c>whizbang.maintenance.rows_affected</c>), the rows it would remove
/// now. Only tasks whose count an index serves are present; a task left out was not measured, which is not zero.
/// </param>
/// <param name="HistoryWalkRemaining">
/// Per event type, the events its incomplete ephemeral history walk has yet to visit.
/// </param>
/// <docs>operations/observability/metrics#pending-reclamation</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MaintenanceMetricsPendingTests.cs</tests>
public sealed record PendingReclamation(
    IReadOnlyDictionary<string, PendingRows> Tasks,
    IReadOnlyDictionary<string, long> HistoryWalkRemaining) {
  /// <summary>A reading of nothing: what an instance that did not take the window reports.</summary>
  public static PendingReclamation None { get; } = new(
    new Dictionary<string, PendingRows>(StringComparer.Ordinal),
    new Dictionary<string, long>(StringComparer.Ordinal));
}

/// <summary>
/// #2365: the streams due for a cut that the seal gate holds (this service's seal, or a consumer's when cuts wait for
/// consumers), per task (<c>truncate</c> | <c>archive</c>), each counted up to a cap.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-instruments</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MaintenanceMetricsEligibleTests.cs</tests>
public sealed record CutsAwaitingSeal {
  /// <summary>No reading: the gauge reports no series.</summary>
  public static CutsAwaitingSeal NotReported { get; } = new();

  /// <summary>True when the reading was taken.</summary>
  public bool Reported { get; init; }

  /// <summary>Per task, the streams held, at most the cap.</summary>
  public IReadOnlyDictionary<string, PendingRows> Tasks { get; init; } = new Dictionary<string, PendingRows>(StringComparer.Ordinal);
}
