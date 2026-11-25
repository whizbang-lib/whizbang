// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The count of stored values read in a form other than the one the persistence profile writes: a
/// temporal read as a rendering rather than the canonical number, an identifier read as the object
/// <c>{"Value": ...}</c> rather than its UUID string, a dictionary read as an object rather than its pairs.
/// </summary>
/// <remarks>
/// <para>
/// A persistence reader accepts the other form for one reason: a row written before the profile
/// settled, or outside the atomic path, would otherwise stop a feature until the next release. The
/// acceptance is transitional, and this is the evidence that decides when it can go. A count of
/// zero across a release cycle means nothing needs it; a count above zero is a rewrite gap with a
/// kind, or a writer with a type, attached, rather than something absorbed in silence.
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
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/CanonicalTemporalReaderToleranceTests.cs</tests>
public static class StoredFormFallbacks {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.Perspectives";

  /// <summary>The counter of renderings read where a canonical number was expected. Tagged by <c>kind</c>.</summary>
  public const string INSTRUMENT_NAME = "whizbang.perspective.temporal_form_fallbacks";

  /// <summary>The counter of identifiers read in the object form where the UUID string was expected. Tagged by <c>type</c>.</summary>
  public const string IDENTIFIER_INSTRUMENT_NAME = "whizbang.perspective.identifier_form_fallbacks";

  /// <summary>The counter of dictionaries read as a JSON object where the array of pairs was expected. Tagged by <c>type</c>.</summary>
  public const string DICTIONARY_INSTRUMENT_NAME = "whizbang.perspective.dictionary_form_fallbacks";
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
  private static Counter<long> _fallbacks = null!;
  private static Counter<long> _identifierFallbacks = null!;
  private static Counter<long> _dictionaryFallbacks = null!;
  private static ILogger _logger = NullLogger.Instance;

  /// <summary>One bit per kind, set once that kind has been announced.</summary>
  private static int _announced;

  /// <summary>The identifier and dictionary types announced so far, so each is announced once.</summary>
  private static readonly HashSet<string> _announcedTypes = [];

  static StoredFormFallbacks() => _createCounters(null);

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
      _createCounters(meterFactory);
      _logger = logger ?? NullLogger.Instance;
      _announced = 0;
      _announcedTypes.Clear();
    }
  }

  /// <summary>Records that an identifier of this type was read in the object form, <c>{"Value": ...}</c>.</summary>
  /// <param name="typeName">The identifier type's name.</param>
  /// <remarks>
  /// A release before #2518 stored a strongly-typed id inside a document as an object; both writers now store it as a
  /// UUID string. The stored-form rewrite converts old rows; until it has, the readers take either form, and this count
  /// is what says the rewrite has not reached a row.
  /// </remarks>
  public static void ObjectFormIdentifierRead(string typeName) {
    ArgumentNullException.ThrowIfNull(typeName);
    _identifierFallbacks.Add(1, new KeyValuePair<string, object?>("type", typeName));

    bool first;
    lock (_gate) {
      first = _announcedTypes.Add(typeName);
    }
    if (first) {
      StoredFormFallbacksLog.ObjectFormIdentifierRead(_logger, typeName, IDENTIFIER_INSTRUMENT_NAME);
    }
  }

  /// <summary>Records that a dictionary of these types was read as a JSON object rather than an array of pairs.</summary>
  /// <param name="dictionaryType">The dictionary type read.</param>
  /// <remarks>
  /// A document written before #1911, by the serializer or into a promoted jsonb column, holds each dictionary as an
  /// object. The stored-form rewrite converts them; until it has, the readers take either form, and this count is what
  /// says the rewrite has not reached a row.
  /// </remarks>
  public static void ObjectFormDictionaryRead(Type dictionaryType) {
    ArgumentNullException.ThrowIfNull(dictionaryType);
    var typeName = TypeNameFormatter.ForLog(dictionaryType);
    _dictionaryFallbacks.Add(1, new KeyValuePair<string, object?>("type", typeName));

    bool first;
    lock (_gate) {
      first = _announcedTypes.Add(typeName);
    }
    if (first) {
      StoredFormFallbacksLog.ObjectFormDictionaryRead(_logger, typeName, DICTIONARY_INSTRUMENT_NAME);
    }
  }

  /// <summary>Records that a stored value of this kind was read as a rendering.</summary>
  /// <param name="kind">The kind read.</param>
  public static void RenderingRead(StoredTemporalKind kind) {
    var name = _kindNames[(int)kind];
    _fallbacks.Add(1, new KeyValuePair<string, object?>("kind", name));

    var bit = 1 << (int)kind;
    if ((Interlocked.Or(ref _announced, bit) & bit) == 0) {
      StoredFormFallbacksLog.RenderingRead(_logger, name, INSTRUMENT_NAME);
    }
  }

  private static void _createCounters(IMeterFactory? meterFactory) {
    // A meter this type made itself is its own to dispose when replaced; one a factory made belongs
    // to the factory.
    _ownMeter?.Dispose();
    _ownMeter = null;

    var meter = meterFactory?.Create(METER_NAME);
    if (meter is null) {
      meter = new Meter(METER_NAME);
      _ownMeter = meter;
    }

    _fallbacks = meter.CreateCounter<long>(
      INSTRUMENT_NAME,
      description: "Stored temporals read as a rendering where the canonical number was expected; tagged by kind");
    _identifierFallbacks = meter.CreateCounter<long>(
      IDENTIFIER_INSTRUMENT_NAME,
      description: "Stored identifiers read in the object form where the UUID string was expected; tagged by type");
    _dictionaryFallbacks = meter.CreateCounter<long>(
      DICTIONARY_INSTRUMENT_NAME,
      description: "Stored dictionaries read as a JSON object where the array of key/value pairs was expected; tagged by type");
  }
}

/// <summary>Source-generated logging for canonical temporal fallbacks.</summary>
internal static partial class StoredFormFallbacksLog {
  [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Warning,
      Message = "A stored {Kind} was read as a rendering rather than the canonical number, so the "
              + "startup rewrite did not reach the path it sits on. Every such read is counted on "
              + "{Instrument} by kind; find the rows with jsonb_typeof and rewrite them, because a "
              + "later release stops accepting renderings")]
  public static partial void RenderingRead(ILogger logger, string kind, string instrument);

  [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Warning,
      Message = "A stored {Type} identifier was read as an object holding a Value rather than as a UUID "
              + "string, so the startup rewrite did not reach the row. Every such read is counted on "
              + "{Instrument} by type; a later release stops accepting the object form")]
  public static partial void ObjectFormIdentifierRead(ILogger logger, string type, string instrument);

  [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Warning,
      Message = "A stored {Type} was read as a JSON object rather than an array of key/value pairs, so the "
              + "startup rewrite did not reach the row. Every such read is counted on {Instrument} by type; "
              + "a later release stops accepting the object form")]
  public static partial void ObjectFormDictionaryRead(ILogger logger, string type, string instrument);
}
