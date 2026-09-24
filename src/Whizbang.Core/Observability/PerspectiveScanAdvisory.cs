using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Observability;

/// <summary>
/// Reports a perspective whose reads are measurably being answered by scanning it.
/// </summary>
/// <remarks>
/// <para>
/// A static check reads the declaration and cannot tell a two hundred row lookup table from one
/// holding two million, and it says nothing about a perspective that was right when it was written
/// and grew afterwards. The case that motivates this is invisible until it is measured: a filter
/// costing nothing for a year, and then a third of a service's database time, with no line of the
/// model having changed in between.
/// </para>
/// <para>
/// So this ranks on what the reading actually cost rather than on what the model looks like. The
/// counter is rows returned by sequential scan, not scans started, because scanning a small table
/// often is cheap and scanning a large one rarely is not; and it is weighed against how much of the
/// same table's reading went through an index, so a table nothing reads by index is distinguished
/// from one where scanning is the rare path.
/// </para>
/// <para>
/// Advice, never action. Promoting a field and building an index changes write cost, storage and
/// locking, and on a large table the build is itself an event. What comes out is a finding, and the
/// host decides what a finding becomes.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/PerspectiveScanAdvisoryTests.cs</tests>
/// <param name="_ledger">Decides whether a finding is new enough to be worth repeating.</param>
/// <param name="_clock">The clock, so the cooldown is testable.</param>
/// <param name="_logger">Where the finding is written.</param>
public sealed partial class PerspectiveScanAdvisory(
    IAdvisoryLedger _ledger, TimeProvider _clock, ILogger<PerspectiveScanAdvisory> _logger) {
  /// <summary>
  /// Rows read by sequential scan before a table is worth mentioning.
  /// </summary>
  /// <remarks>
  /// Set where a table has to have been genuinely expensive to reach it. A lower bound would
  /// reproduce the static check's noise with more machinery behind it, which is the one outcome
  /// this is meant to avoid.
  /// </remarks>
  public const long DEFAULT_ROWS_THRESHOLD = 10_000_000;

  /// <summary>How long the same unchanged advice stays suppressed.</summary>
  public static readonly TimeSpan DEFAULT_REPORT_COOLDOWN = TimeSpan.FromDays(7);

  /// <summary>
  /// The tables whose reading is dominated by scanning, reported once per cooldown.
  /// </summary>
  /// <param name="statistics">Scan counters per table.</param>
  /// <param name="tableSizes">Table sizes per table, for the finding's context.</param>
  /// <param name="rowsThreshold">Rows read by scan before a table is worth mentioning.</param>
  /// <param name="cancellationToken">Cancels the consult.</param>
  /// <returns>The findings raised, for a host to route.</returns>
  public async ValueTask<IReadOnlyList<SystemEvents.PerspectiveScanAdvised>> ReportAsync(
      IReadOnlyDictionary<string, TableScanStatistics> statistics,
      IReadOnlyDictionary<string, long> tableSizes,
      long rowsThreshold = DEFAULT_ROWS_THRESHOLD,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(statistics);
    ArgumentNullException.ThrowIfNull(tableSizes);

    var reported = new List<SystemEvents.PerspectiveScanAdvised>();
    var now = _clock.GetUtcNow();

    foreach (var (table, scans) in statistics) {
      // A perspective's table, and nothing else. The framework's own queues are scanned by design.
      if (!table.StartsWith("wh_per_", StringComparison.Ordinal) || scans.SequentialRowsRead < rowsThreshold) {
        continue;
      }

      // A table read mostly by index is doing what it should; the occasional scan is not a finding.
      // Comparing scans rather than rows here on purpose: a single index scan and a single
      // sequential scan are one read each, whatever each returned.
      if (scans.IndexScans > scans.SequentialScans) {
        continue;
      }

      tableSizes.TryGetValue(table, out var bytes);

      // The signature is what the advice says rather than that it was said. Reading that has grown
      // by an order of magnitude is different advice and is reported at once; reading that has
      // merely continued is the same advice and waits out the cooldown.
      var magnitude = scans.SequentialRowsRead.ToString("E0", System.Globalization.CultureInfo.InvariantCulture);

      if (await _ledger.TryBeginReportAsync(
            $"scan:{table}", magnitude, now, DEFAULT_REPORT_COOLDOWN, cancellationToken).ConfigureAwait(false)) {
        LogPerspectiveIsScanned(
          _logger, table, scans.SequentialRowsRead, scans.SequentialScans, scans.IndexScans, bytes / (1024 * 1024));

        reported.Add(new SystemEvents.PerspectiveScanAdvised {
          TableName = table,
          SequentialRowsRead = scans.SequentialRowsRead,
          SequentialScans = scans.SequentialScans,
          IndexScans = scans.IndexScans,
          TableSizeBytes = bytes,
          RowsThreshold = rowsThreshold,
        });
      }
    }

    return reported;
  }

  [LoggerMessage(
    EventId = 306,
    Level = LogLevel.Warning,
    Message = "Perspective table {Table} has returned {SequentialRowsRead} rows by sequential scan across "
            + "{SequentialScans} scans against {IndexScans} index scans, at {SizeMegabytes} MB. Its reads are "
            + "being answered by reading it. Promote the filtered property with [PhysicalField] plus [Indexed], "
            + "or declare an index that covers the filter, and the reads become lookups.")]
  private static partial void LogPerspectiveIsScanned(
    ILogger logger, string table, long sequentialRowsRead, long sequentialScans, long indexScans, long sizeMegabytes);
}
