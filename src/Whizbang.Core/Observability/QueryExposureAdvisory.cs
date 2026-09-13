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
/// Once per model per process. This runs on the statistics cycle, so repeating would turn one
/// finding into a log entry every cycle forever, and a warning that repeats without changing gets
/// filtered out at the collector.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/QueryExposureAdvisoryTests.cs</tests>
/// <param name="logger">
/// Where findings go. Required rather than optional: an optional injected logger is silently absent
/// wherever the type is built by hand, and this type is built by hand, so the argument is the one
/// thing a call site must not be able to forget.
/// </param>
public sealed partial class QueryExposureAdvisory(ILogger<QueryExposureAdvisory> logger) {
  private readonly ILogger<QueryExposureAdvisory> _logger =
    logger ?? throw new ArgumentNullException(nameof(logger));

  private readonly Lock _reportedLock = new();
  private readonly HashSet<Type> _reported = [];

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
  /// <returns>How many models were reported on this call, not counting ones already reported.</returns>
  public int Report(
      IReadOnlyDictionary<string, long> tableSizes,
      ICollectiveSiblingTableSource? tables,
      long thresholdBytes = DEFAULT_SIZE_THRESHOLD_BYTES) {
    ArgumentNullException.ThrowIfNull(tableSizes);

    if (tables is null) {
      return 0;
    }

    var reported = 0;

    foreach (var (model, exposure) in QueryExposureRegistry.All()) {
      if (!QueryExposureRegistry.CanBeOrdered(model) || _alreadyReported(model)) {
        continue;
      }

      var table = _tableFor(tables, model);
      if (table is null || !tableSizes.TryGetValue(table, out var bytes) || bytes < thresholdBytes) {
        continue;
      }

      if (_claim(model)) {
        LogExposedPerspectiveIsLarge(
          _logger, model.Name, table, bytes / (1024 * 1024), exposure.ToString(), null);
        reported++;
      }
    }

    return reported;
  }

  private bool _alreadyReported(Type model) {
    lock (_reportedLock) {
      return _reported.Contains(model);
    }
  }

  /// <summary>Takes the report for this model, or declines if another caller already has it.</summary>
  private bool _claim(Type model) {
    lock (_reportedLock) {
      return _reported.Add(model);
    }
  }

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
        + "serves, reading the whole table. Declare [Indexed] on the fields the surface offers, "
        + "[IndexAllFields] if it is queried every way, or record the decision with "
        + "[SuppressIndexAdvisory(\"reason\")]. Reported once per process.")]
  static partial void LogExposedPerspectiveIsLarge(
    ILogger logger, string ModelName, string TableName, long SizeMegabytes, string Exposure, Exception? exception);
}
