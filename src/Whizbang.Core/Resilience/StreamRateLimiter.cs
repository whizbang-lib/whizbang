using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Resilience;

/// <summary>
/// Per-stream event rate limiter with throttle/cooldown.
/// Detects runaway event loops by tracking handler invocations per stream
/// within a sliding window. When the threshold is exceeded, the stream is
/// paused for a configurable cooldown period, then allowed to resume.
/// </summary>
/// <remarks>
/// Throttle pattern: 50 events → 30s pause → 50 events → 30s pause.
/// This slows runaway loops without permanently blocking the stream.
/// </remarks>
/// <docs>resilience/stream-rate-limiter</docs>
public sealed partial class StreamRateLimiter {
  private readonly StreamRateLimiterOptions _options;
  private readonly ILogger _logger;
  private readonly ConcurrentDictionary<Guid, StreamState> _streams = new();
  private int _callCount;

  private record struct StreamState(int Count, DateTimeOffset WindowStart, DateTimeOffset? CooldownUntil);

  /// <summary>
  /// Creates a new stream rate limiter with the specified options.
  /// </summary>
  public StreamRateLimiter(StreamRateLimiterOptions options, ILogger<StreamRateLimiter>? logger = null) {
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamRateLimiter>.Instance;
  }

  /// <summary>
  /// Returns true if the stream is within limits. Returns false if throttled.
  /// When threshold is hit: logs Warning, pauses stream for CooldownDuration.
  /// After cooldown expires, stream resumes with a fresh window.
  /// </summary>
  /// <param name="streamId">The stream to check/acquire against</param>
  /// <returns>True if allowed to proceed, false if rate-limited</returns>
  public bool TryAcquire(Guid streamId) {
    var now = DateTimeOffset.UtcNow;

    // Lazy cleanup of stale entries every 100 calls
    if (Interlocked.Increment(ref _callCount) % 100 == 0) {
      _pruneStaleEntries(now);
    }

    var state = _streams.AddOrUpdate(
      streamId,
      _ => new StreamState(1, now, null),
      (_, existing) => _updateState(existing, now));

    if (state.CooldownUntil.HasValue && state.CooldownUntil.Value > now) {
      return false; // In cooldown — throttled
    }

    if (state.Count > _options.MaxEventsPerWindow) {
      // Threshold exceeded — enter cooldown
      var cooldownUntil = _options.CooldownDuration > TimeSpan.Zero
        ? now + _options.CooldownDuration
        : (DateTimeOffset?)null; // Zero cooldown = immediate resume

      _streams[streamId] = new StreamState(state.Count, state.WindowStart, cooldownUntil);

      LogStreamRateLimited(_logger, streamId, state.Count,
        (now - state.WindowStart).TotalSeconds, _options.CooldownDuration.TotalSeconds);

      return _options.CooldownDuration <= TimeSpan.Zero; // If zero cooldown, allow immediately
    }

    return true;
  }

  private StreamState _updateState(StreamState existing, DateTimeOffset now) {
    // If in cooldown and cooldown has expired → reset everything
    if (existing.CooldownUntil.HasValue && existing.CooldownUntil.Value <= now) {
      return new StreamState(1, now, null);
    }

    // If in cooldown and not expired → keep cooldown state, don't increment
    if (existing.CooldownUntil.HasValue) {
      return existing;
    }

    // If window expired → reset count
    if (now - existing.WindowStart > _options.WindowDuration) {
      return new StreamState(1, now, null);
    }

    // Normal: increment count within window
    return new StreamState(existing.Count + 1, existing.WindowStart, null);
  }

  private void _pruneStaleEntries(DateTimeOffset now) {
    foreach (var kvp in _streams) {
      var age = now - kvp.Value.WindowStart;
      if (age > _options.StaleEntryTimeout) {
        _streams.TryRemove(kvp.Key, out _);
      }
    }
  }

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Stream {StreamId} rate-limited: {Count} events in {WindowSeconds:F1}s. Pausing for {CooldownSeconds}s.")]
  private static partial void LogStreamRateLimited(
    ILogger logger, Guid streamId, int count, double windowSeconds, double cooldownSeconds);
}
