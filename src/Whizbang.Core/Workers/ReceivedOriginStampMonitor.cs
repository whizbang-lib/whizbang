using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Watches the receive path for events that arrive without the producer's origin stamp. Stream integrity
/// attributes every received event to the service that produced it; an inbox row with no source is stored
/// as locally originated, and checkpoints, gap detection and the audit cannot see it. That failure is
/// silent by construction (#1029: every receiving service stored zero stamped events for weeks), so when
/// gap detection or the audit is enabled this monitor counts received events with and without an origin
/// and, once per <see cref="Window"/> in which any lacked one, logs a warning naming how many.
/// </summary>
/// <remarks>
/// The cost is two interlocked operations and a timestamp read per received event; nothing is queried.
/// Control-plane traffic is excluded, since it is published straight to the transport and never carries an
/// origin. A window is evaluated by the first event received after it closes, so an idle service logs
/// nothing.
/// </remarks>
/// <docs>resilience/stream-integrity#origin-stamp</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ReceivedOriginStampMonitorTests.cs</tests>
internal sealed partial class ReceivedOriginStampMonitor {
  /// <summary>How often the counts are evaluated, and so the most often the warning can repeat.</summary>
  internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

  private readonly ILogger _logger;
  private readonly TimeProvider _timeProvider;
  private readonly bool _enabled;
  private long _stamped;
  private long _unstamped;
  private long _windowStart;

  /// <summary>Creates the monitor; it records nothing unless gap detection or the audit is enabled.</summary>
  public ReceivedOriginStampMonitor(
      IOptions<StreamIntegrityOptions> options,
      ILogger<ReceivedOriginStampMonitor>? logger = null,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(options);
    _enabled = options.Value.GapDetectionEnabled || options.Value.AuditEnabled;
    _logger = logger ?? NullLogger<ReceivedOriginStampMonitor>.Instance;
    _timeProvider = timeProvider ?? TimeProvider.System;
    _windowStart = _timeProvider.GetTimestamp();
  }

  /// <summary>Counts one received inbox row and, when the window has closed, evaluates it.</summary>
  internal void Record(InboxMessage row) {
    if (!_enabled || !row.IsEvent || ControlPlaneTypeRegistry.IsControlPlane(row.MessageType)) {
      return;
    }
    if (row.SourceServiceId == Guid.Empty) {
      Interlocked.Increment(ref _unstamped);
    } else {
      Interlocked.Increment(ref _stamped);
    }

    var start = Interlocked.Read(ref _windowStart);
    if (_timeProvider.GetElapsedTime(start) < Window
        || Interlocked.CompareExchange(ref _windowStart, _timeProvider.GetTimestamp(), start) != start) {
      return;   // the window is open, or a concurrent receive already closed it
    }
    var unstamped = Interlocked.Exchange(ref _unstamped, 0);
    var received = unstamped + Interlocked.Exchange(ref _stamped, 0);
    if (unstamped > 0) {
      LogUnstampedReceives(_logger, unstamped, received, (int)Window.TotalMinutes);
    }
  }

  [LoggerMessage(EventId = 1029, Level = LogLevel.Warning,
    Message = "Stream integrity is enabled, but {Unstamped} of {Received} events received in the last {WindowMinutes} minutes " +
              "carried no origin service id. They are stored as locally originated, so checkpoints, gap detection and the " +
              "audit cannot attribute them. Check that the producing services run a version that stamps the origin and " +
              "that their outbox resolved a service id at startup.")]
  private static partial void LogUnstampedReceives(ILogger logger, long unstamped, long received, int windowMinutes);
}
