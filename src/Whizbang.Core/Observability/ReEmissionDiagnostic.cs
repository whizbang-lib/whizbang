using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Observability;

/// <summary>
/// <para>Runtime re-emission cascade diagnostic (issue #587). A service that PUBLISHES an
/// event type it also CONSUMES is the amplification signature: each hop can re-raise what
/// it just handled, multiplying a bulk operation's writes hop over hop — and nothing
/// surfaced it. Detection is runtime observation at the publish seam: the emitted type is
/// checked against the receptor registry's consumed set; a match counts on
/// <c>whizbang.dispatcher.re_emissions</c> (tagged by type) and warns once per type.</para>
/// <para>Deliberately a diagnostic, not a gate: consume-and-re-raise is sometimes a
/// legitimate pattern (enrichment pipelines), so the framework surfaces the shape and the
/// operator judges it. Zero reflection: type names come from the wire formatter and the
/// source-generated registry.</para>
/// </summary>
/// <docs>fundamentals/messaging/publishing-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/ReEmissionDiagnosticTests.cs</tests>
public sealed partial class ReEmissionDiagnostic {
  private readonly ILogger<ReEmissionDiagnostic> _logger;
  private readonly DispatcherMetrics? _metrics;
  private readonly HashSet<string>? _consumedTypes;
  private readonly ConcurrentDictionary<string, bool> _warned = new(StringComparer.Ordinal);

  /// <summary>Builds the diagnostic; a null registry (or one exposing no handled messages) leaves it inert.</summary>
  public ReEmissionDiagnostic(
      IReceptorRegistryQuery? registryQuery,
      ILogger<ReEmissionDiagnostic>? logger,
      DispatcherMetrics? metrics) {
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReEmissionDiagnostic>.Instance;
    _metrics = metrics;
    var handled = registryQuery?.GetHandledMessages();
    if (handled is { Count: > 0 }) {
      _consumedTypes = new HashSet<string>(StringComparer.Ordinal);
      foreach (var info in handled) {
        _consumedTypes.Add(EventTypeMatchingHelper.NormalizeTypeName(info.MessageTypeName));
      }
    }
  }

  /// <summary>
  /// Records one emission of <paramref name="wireTypeName"/>. When the type is in this
  /// service's own consumed set, the re-emission counter increments (tagged by type) and a
  /// warning logs on the type's FIRST sighting — a cascade under load must not become its
  /// own log storm.
  /// </summary>
  public void RecordEmission(string wireTypeName) {
    if (_consumedTypes is null || string.IsNullOrEmpty(wireTypeName)) {
      return;
    }
    var normalized = EventTypeMatchingHelper.NormalizeTypeName(wireTypeName);
    if (!_consumedTypes.Contains(normalized)) {
      return;
    }
    _metrics?.ReEmissions.Add(1, new KeyValuePair<string, object?>("type", normalized));
    if (_warned.TryAdd(normalized, true)) {
      LogReEmission(_logger, normalized);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "This service PUBLISHES '{MessageType}', a type it also CONSUMES — the re-emission cascade shape: "
            + "each hop can re-raise what it just handled, multiplying writes hop over hop. Deliberate enrichment "
            + "pipelines are fine; an unintended echo is an amplification bug. Trend: whizbang.dispatcher.re_emissions")]
  static partial void LogReEmission(ILogger logger, string messageType);
}
