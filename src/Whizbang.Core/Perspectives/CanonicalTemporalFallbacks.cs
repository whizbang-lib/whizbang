using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The count of stored temporals read in a form other than the canonical number.
/// </summary>
/// <remarks>
/// <para>
/// A canonical reader accepts a rendering for one reason: a row the rewrite has not reached would
/// otherwise stop a feature until the next release. The acceptance is transitional, and this is the
/// evidence that decides when it can go. A count of zero across a release cycle means the rewrite
/// reached everything; a count above zero is a rewrite gap with a kind attached, rather than
/// something absorbed in silence.
/// </para>
/// <para>
/// Static, because a converter is constructed by the serializer with nothing injected into it. The
/// host configures the meter factory and the logger once at startup; until then the meter is a
/// plain one under the same name, which an OpenTelemetry provider listening by name still sees.
/// </para>
/// <para>
/// The first rendering of each kind is announced once at Warning, with the instrument to watch.
/// Once, because the count is on the meter and a log line per row would be a log storm on exactly
/// the deployment that most needs to read its logs.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CanonicalTemporalReaderToleranceTests.cs</tests>
public static class CanonicalTemporalFallbacks {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.Perspectives";

  /// <summary>The counter of renderings read where a canonical number was expected. Tagged by <c>kind</c>.</summary>
  public const string INSTRUMENT_NAME = "whizbang.perspective.temporal_form_fallbacks";
#pragma warning restore CA1707

  /// <summary>The kind names as tag values, indexed by the enum, so a read allocates nothing.</summary>
  private static readonly string[] _kindNames = [
    nameof(StoredTemporalKind.Instant),
    nameof(StoredTemporalKind.OffsetInstant),
    nameof(StoredTemporalKind.Day),
    nameof(StoredTemporalKind.TimeOfDay),
    nameof(StoredTemporalKind.Duration),
  ];

  private static readonly Lock _gate = new();
  private static Meter? _ownMeter;
  private static Counter<long> _fallbacks = _createCounter(null);
  private static ILogger _logger = NullLogger.Instance;

  /// <summary>One bit per kind, set once that kind has been announced.</summary>
  private static int _announced;

  /// <summary>
  /// Gives the fallbacks a meter factory and a logger. Called once by the host at startup.
  /// </summary>
  /// <param name="meterFactory">The factory to create the meter with, or <see langword="null"/> for a plain meter.</param>
  /// <param name="logger">The logger to announce on, or <see langword="null"/> to announce nowhere.</param>
  /// <remarks>
  /// Configuring again starts the announcements over. A process configures once; a test configures
  /// per test, and needs each to see its own first announcement.
  /// </remarks>
  public static void Configure(IMeterFactory? meterFactory, ILogger? logger) {
    lock (_gate) {
      _fallbacks = _createCounter(meterFactory);
      _logger = logger ?? NullLogger.Instance;
      _announced = 0;
    }
  }

  /// <summary>Records that a stored value of this kind was read as a rendering.</summary>
  /// <param name="kind">The kind read.</param>
  public static void RenderingRead(StoredTemporalKind kind) {
    var name = _kindNames[(int)kind];
    _fallbacks.Add(1, new KeyValuePair<string, object?>("kind", name));

    var bit = 1 << (int)kind;
    if ((Interlocked.Or(ref _announced, bit) & bit) == 0) {
      CanonicalTemporalFallbacksLog.RenderingRead(_logger, name, INSTRUMENT_NAME);
    }
  }

  private static Counter<long> _createCounter(IMeterFactory? meterFactory) {
    // A meter this type made itself is its own to dispose when replaced; one a factory made belongs
    // to the factory.
    _ownMeter?.Dispose();
    _ownMeter = null;

    var meter = meterFactory?.Create(METER_NAME);
    if (meter is null) {
      meter = new Meter(METER_NAME);
      _ownMeter = meter;
    }

    return meter.CreateCounter<long>(
      INSTRUMENT_NAME,
      description: "Stored temporals read as a rendering where the canonical number was expected; tagged by kind");
  }
}

/// <summary>Source-generated logging for canonical temporal fallbacks.</summary>
internal static partial class CanonicalTemporalFallbacksLog {
  [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Warning,
      Message = "A stored {Kind} was read as a rendering rather than the canonical number, so the "
              + "startup rewrite did not reach the path it sits on. Every such read is counted on "
              + "{Instrument} by kind; find the rows with jsonb_typeof and rewrite them, because a "
              + "later release stops accepting renderings")]
  public static partial void RenderingRead(ILogger logger, string kind, string instrument);
}
