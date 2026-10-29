// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Measuring a table's bloat with <c>pgstattuple_approx</c> while the pgstattuple capability is Ready (#1807): before a
/// table the estimate shortlisted enters the rewrite queue, and after a rewrite to tell whether it shrank.
/// </summary>
/// <remarks>
/// <para>
/// The catalog estimate runs every statistics pass and costs nothing, but it guesses. <c>pgstattuple_approx</c> reads
/// the visibility map and scans only the pages that are not all-visible, so it is cheap on a mostly static table and
/// is asked for only where the estimate has already said a rewrite may be owed. The exact <c>pgstattuple</c> scan reads
/// the whole table and is never asked for.
/// </para>
/// <para>
/// A measurement that fails is logged with the table and the failure, and the estimate stands in for it: that is
/// exactly where the cycle would be without pgstattuple, so a role that cannot call the function, or a measurement
/// that times out, costs the confirmation and nothing else.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerBloatMeasurementTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/OnlineTableRewriteStepTests.cs:WhenPgstattupleIsReady_TheReMeasureIsApprox_AndItDecidesTheClearAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepTests.cs:WhenPgstattupleIsReady_TheReMeasureIsApprox_AndItDecidesTheClearAsync</tests>
internal static partial class ApproxBloatMeasurement {
  /// <summary>The pgstattuple extension, as the capability registry identifies it.</summary>
  internal static readonly CapabilityId Capability = new(RequiresPostgresExtensionAttribute.PROVIDER, "pgstattuple");

  /// <summary>
  /// How far apart a measurement and its estimate may be, as a share of the estimate, before the two are logged
  /// together: a quarter, as the log line states. Closer than that is the noise between a sampled row width and a counted row; further apart
  /// means the estimate was reading stale statistics.
  /// </summary>
  internal const double DISAGREEMENT_TOLERANCE = 0.25;

  /// <summary>Whether pgstattuple is Ready on every database the registry probed it on, and was probed at all.</summary>
  /// <param name="registry">The capability registry, or null when the host registers none.</param>
  /// <returns>True when a measurement may be asked for.</returns>
  internal static bool IsReady(ICapabilityRegistry? registry) {
    if (registry is null) {
      return false;
    }
    var statuses = registry.Statuses.Where(s => s.Descriptor.Id == Capability).ToArray();
    return statuses.Length > 0 && Array.TrueForAll(statuses, s => s.State == CapabilityState.Ready);
  }

  /// <summary>Measures <paramref name="table"/> with pgstattuple_approx; null when it has no answer or the measurement failed (logged).</summary>
  internal static async Task<double?> TryMeasureAsync(
      IWorkCoordinator coordinator, string table, ILogger logger, CancellationToken cancellationToken) {
    try {
      return await coordinator.MeasureTableBloatAsync(table, BloatMeasurementMethod.Approx, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogMeasureFailed(logger, table, ex);
      return null;
    }
  }

  /// <summary>Logs the two figures when a measurement and the estimate it was taken to confirm are further apart than the tolerance.</summary>
  internal static void ReportDisagreement(ILogger logger, string table, double estimate, BloatMeasurement measured) {
    if (Math.Abs(measured.Ratio - estimate) > estimate * DISAGREEMENT_TOLERANCE) {
      var method = measured.Method.ToValue();
      LogDisagreement(logger, table, estimate, method, measured.Ratio);
    }
  }

  /// <summary>
  /// The ratio after a rewrite: measured with pgstattuple_approx when <paramref name="approxReady"/> and the
  /// measurement answers, otherwise the rewriter's own catalog re-measure; null when nothing was rewritten.
  /// </summary>
  internal static async Task<BloatMeasurement?> AfterRewriteAsync(
      IWorkCoordinator coordinator, string table, double? rewriterRatio, bool approxReady, ILogger logger,
      CancellationToken cancellationToken) {
    if (rewriterRatio is not { } estimate) {
      return null;
    }
    if (approxReady && await TryMeasureAsync(coordinator, table, logger, cancellationToken).ConfigureAwait(false) is { } approx) {
      return new BloatMeasurement(approx, BloatMeasurementMethod.Approx);
    }
    return new BloatMeasurement(estimate, BloatMeasurementMethod.Estimate);
  }

  // Event ids outside the ranges the calling workers use, since these log under the caller's category.
  [LoggerMessage(EventId = 900, Level = LogLevel.Warning,
    Message = "Measuring the bloat of {Table} with pgstattuple_approx failed; the estimate stands in for it")]
  static partial void LogMeasureFailed(ILogger logger, string table, Exception exception);

  [LoggerMessage(EventId = 901, Level = LogLevel.Information,
    Message = "Bloat of {Table}: the estimate {Estimate}x and the {Method} measurement {Measured}x disagree by more than 25%")]
  static partial void LogDisagreement(ILogger logger, string table, double estimate, string method, double measured);
}
