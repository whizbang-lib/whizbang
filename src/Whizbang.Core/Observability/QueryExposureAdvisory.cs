using Microsoft.Extensions.Logging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Observability;

/// <summary>
/// Tells an operator when a perspective a request can sort by has grown large enough that the
/// missing index costs real time.
/// </summary>
/// <remarks>
/// <para>
/// WHIZ306 raises the same concern at build time and cannot finish the argument, because it has no
/// idea how big the table is. A model exposed to request-time sorting with a few hundred rows behind
/// it is fine, and warning about it is how a build warning gets suppressed wholesale. The same
/// exposure over a table of several gigabytes is the most expensive query shape there is, and the
/// least likely to be noticed, because it returns correct rows.
/// </para>
/// <para>
/// Size rather than row count because that is what the catalog gives without a scan, and it is the
/// better proxy anyway: what an unindexed sort costs is the bytes it has to read.
/// </para>
/// <para>
/// Once per finding, not once per cycle: this runs on the statistics cycle, so repeating would turn
/// one finding into a log entry every cycle forever, and a warning that repeats without changing
/// gets filtered out at the collector. How long "once" lasts belongs to the ledger, because the
/// answer is a property of the deployment rather than of this type. A single long-lived instance
/// can hold it in memory; a fleet cannot, since every replica reaches the same conclusion about the
/// same table and every restart forgets.
/// </para>
/// <para>
/// Advice that nobody acted on comes back after <see cref="DEFAULT_REPORT_COOLDOWN"/>, and advice
/// that has CHANGED comes back at once. A finding suppressed forever is indistinguishable from a
/// finding that was fixed, and the table is still being scanned either way.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/QueryExposureAdvisoryTests.cs</tests>
/// <param name="logger">
/// Where findings go. Required rather than optional: an optional injected logger is silently absent
/// wherever the type is built by hand, and this type is built by hand, so the argument is the one
/// thing a call site must not be able to forget.
/// </param>
/// <param name="ledger">
/// What has already been said. Required for the same reason as the logger, and with more at stake:
/// a default would be the per-process one, so a deployment would quietly get per-replica advice on
/// every restart and look exactly like a deployment that had chosen that.
/// </param>
/// <param name="timeProvider">The clock the cooldown is measured on. Defaults to the system clock.</param>
public sealed partial class QueryExposureAdvisory(
    ILogger<QueryExposureAdvisory> logger,
    IAdvisoryLedger ledger,
    TimeProvider? timeProvider = null) {
  private readonly ILogger<QueryExposureAdvisory> _logger =
    logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly IAdvisoryLedger _ledger =
    ledger ?? throw new ArgumentNullException(nameof(ledger));
  private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

  /// <summary>
  /// The size a perspective has to reach before an unindexed sort over it is worth an operator's
  /// attention.
  /// </summary>
  /// <remarks>
  /// Deliberately high. The advisory's value is that it is rare enough to be read, and a threshold
  /// that catches every small lookup table would make it noise. A quarter of a gigabyte is already
  /// well past the point where a sequential scan shows up in a request's latency.
  /// </remarks>
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  public const long DEFAULT_SIZE_THRESHOLD_BYTES = 256L * 1024 * 1024;
#pragma warning restore CA1707

  /// <summary>
  /// How long the same unchanged advice stays quiet before it is worth saying again.
  /// </summary>
  /// <remarks>
  /// A week, because acting on this means adding a column and building an index on a large table,
  /// which is scheduled work rather than something done on the spot. Short enough that a finding
  /// nobody acted on does not disappear, long enough that it never competes with anything urgent.
  /// </remarks>
#pragma warning disable CA1707
  public static readonly TimeSpan DEFAULT_REPORT_COOLDOWN = TimeSpan.FromDays(7);
#pragma warning restore CA1707

  /// <summary>
  /// Reports each newly oversized perspective that a request can order by.
  /// </summary>
  /// <param name="tableSizes">Estimated table sizes in bytes, by table name.</param>
  /// <param name="tables">
  /// Resolves a model to its physical table. When null, nothing can be reported: the exposure is
  /// known but the table it lands on is not.
  /// </param>
  /// <param name="thresholdBytes">
  /// The size to report above. Defaults to <see cref="DEFAULT_SIZE_THRESHOLD_BYTES"/>.
  /// </param>
  /// <param name="cancellationToken">Cancels the ledger consult.</param>
  /// <returns>
  /// The findings made on this call, not including the ones the ledger has already reported.
  /// Returned rather than counted so the caller can emit them, which the caller does after this
  /// returns: the log line and the emitted record are the same finding and must share one
  /// suppression decision, so the decision is taken here and the dispatch is not.
  /// </returns>
  public async ValueTask<IReadOnlyList<SystemEvents.PerspectiveIndexAdvised>> ReportAsync(
      IReadOnlyDictionary<string, long> tableSizes,
      ICollectiveSiblingTableSource? tables,
      long thresholdBytes = DEFAULT_SIZE_THRESHOLD_BYTES,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(tableSizes);

    if (tables is null) {
      return [];
    }

    var reported = new List<SystemEvents.PerspectiveIndexAdvised>();
    var now = _clock.GetUtcNow();

    foreach (var (model, exposure) in QueryExposureRegistry.All()) {
      if (!QueryExposureRegistry.CanBeOrdered(model)) {
        continue;
      }

      // A model that indexed what it exposes, asked for every field, or recorded a decision with
      // [SuppressIndexAdvisory] registers no unaccounted fields. All three are an author having
      // already answered this, and repeating the question at runtime is how a suppression stops
      // meaning anything.
      var unindexed = QueryExposureRegistry.UnindexedFields(model);
      if (unindexed.Count == 0) {
        continue;
      }

      var table = _tableFor(tables, model);
      if (table is null || !tableSizes.TryGetValue(table, out var bytes) || bytes < thresholdBytes) {
        continue;
      }

      if (await _ledger.TryBeginReportAsync(
            _findingKey(model, table), _signature(exposure, unindexed), now, DEFAULT_REPORT_COOLDOWN,
            cancellationToken).ConfigureAwait(false)) {
        LogExposedPerspectiveIsLarge(
          _logger, model.Name, table, bytes / (1024 * 1024), exposure.ToString(),
          unindexed.Count, string.Join(", ", unindexed));

        // The same finding as the log line, in a shape a host can route. A log line is the end of
        // the road: one deployment raises a work item from this, another only records it, and the
        // framework should not be deciding which.
        reported.Add(new SystemEvents.PerspectiveIndexAdvised {
          ModelName = model.Name,
          TableName = table,
          TableSizeBytes = bytes,
          ThresholdBytes = thresholdBytes,
          Exposure = exposure.ToString(),
          UnindexedFields = [.. unindexed],
        });
      }
    }

    return reported;
  }

  /// <summary>What the finding is about, in a form that means the same thing in every process.</summary>
  /// <remarks>
  /// The model's name and its table, not the <see cref="Type"/> itself: a durable ledger has to
  /// recognize the same finding after a restart and in another replica, and a runtime handle cannot
  /// do that. Both halves, because the table is what an operator acts on and the model is what an
  /// author would have to change. Rendered through the formatter that every other durable use of a
  /// type name goes through, so this key agrees with them rather than nearly agreeing.
  /// </remarks>
  private static string _findingKey(Type model, string table) =>
    $"perspective-index:{TypeNameFormatter.GetPerspectiveName(model)}:{table}";

  /// <summary>
  /// What the finding says, so that advice which has changed is not mistaken for advice already
  /// given.
  /// </summary>
  /// <remarks>
  /// Ordered, because the set is what matters and the order it was discovered in is not. Without
  /// that, indexing one field of three could leave the signature unchanged, or reorder it and
  /// re-raise advice that had not changed at all.
  /// </remarks>
  private static string _signature(QueryExposures exposure, IReadOnlyCollection<string> unindexed) =>
    $"{exposure}:{string.Join(",", unindexed.Order(StringComparer.Ordinal))}";

  /// <summary>
  /// The model's table, or null when the driver does not know it.
  /// </summary>
  /// <remarks>
  /// <see cref="ICollectiveSiblingTableSource.TableFor"/> throws for a model it has no table for,
  /// which is right for a query that cannot be built without one and wrong here: a model registered
  /// as exposed but absent from this driver's map is a reason to say nothing, not to take down the
  /// statistics cycle.
  /// </remarks>
  private static string? _tableFor(ICollectiveSiblingTableSource tables, Type model) {
    try {
      return tables.TableFor(model);
    } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException) {
      return null;
    }
  }

  [LoggerMessage(
    EventId = 70,
    Level = LogLevel.Warning,
    Message = "Perspective '{ModelName}' ({TableName}) is {SizeMegabytes} MB and a request can shape "
        + "its query ({Exposure}), so any field it names can reach an ORDER BY or WHERE that no index "
        + "serves, reading the whole table. {UnindexedCount} of its fields carry no index and no "
        + "recorded decision ({UnindexedFields}). Declare [Indexed] on the fields the surface offers, "
        + "[IndexAllFields] if it is queried every way, or record the decision with "
        + "[SuppressIndexAdvisory(\"reason\")]. Reported once until it changes.")]
  static partial void LogExposedPerspectiveIsLarge(
    ILogger logger, string ModelName, string TableName, long SizeMegabytes, string Exposure,
    int UnindexedCount, string UnindexedFields);
}
