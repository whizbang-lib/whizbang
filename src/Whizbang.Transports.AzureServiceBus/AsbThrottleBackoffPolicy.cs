using Azure.Messaging.ServiceBus;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Accept-pressure governor for namespace throttling. When the broker answers
/// <see cref="ServiceBusFailureReason.ServiceBusy"/> (error 50009, "namespace is being throttled
/// — wait and try again"), every concurrent session-accept slot that keeps retrying AMPLIFIES the
/// throttle: N services × M subscriptions × MaxConcurrentSessions accept polls keep the namespace
/// pinned, and no consumer can accept a single session while messages pile up broker-side
/// (observed live, fleet-wide). This policy converts that into ONE polite pause per processor —
/// exponentially longer per consecutive throttle, capped, reset after quiet — and single-flights
/// the pause so sibling slots reporting the same throttle never stack stop/start cycles.
/// </summary>
/// <param name="baseDelay">First pause (the broker's own guidance is ~2 seconds).</param>
/// <param name="maxDelay">Pause ceiling — an unbounded pause would turn a blip into an outage.</param>
/// <param name="quietReset">A throttle after this much quiet is a NEW incident (streak resets).</param>
/// <docs>transports/azure-service-bus</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbThrottleBackoffPolicyTests.cs</tests>
public sealed class AsbThrottleBackoffPolicy(TimeSpan baseDelay, TimeSpan maxDelay, TimeSpan quietReset) {
  private readonly object _lock = new();
  private int _streak;
  private DateTimeOffset? _lastThrottle;
  private bool _pauseInFlight;

  /// <summary>
  /// Records a processor error. Returns the pause this processor should take before accepting
  /// again, or null when the error is not namespace pressure (ordinary errors keep receiving).
  /// </summary>
  public TimeSpan? RecordError(ServiceBusFailureReason reason, DateTimeOffset now) {
    if (reason != ServiceBusFailureReason.ServiceBusy) {
      return null;
    }
    lock (_lock) {
      if (_lastThrottle is { } last && now - last > quietReset) {
        _streak = 0;   // quiet stretch — a fresh incident starts a fresh streak.
      }
      _lastThrottle = now;
      _streak++;
      var doublings = Math.Min(_streak - 1, 30);   // 2^30 guards TimeSpan overflow; maxDelay caps below.
      var pause = baseDelay * Math.Pow(2, doublings);
      return pause > maxDelay ? maxDelay : pause;
    }
  }

  /// <summary>
  /// Claims the single-flight pause. True = the caller owns stopping and resuming the processor;
  /// false = a sibling slot already holds it (absorb the report, take no action). Concurrent
  /// accept slots all hit the same throttle within milliseconds — one pause, not N.
  /// </summary>
  public bool TryBeginPause() {
    lock (_lock) {
      if (_pauseInFlight) {
        return false;
      }
      _pauseInFlight = true;
      return true;
    }
  }

  /// <summary>Releases the single-flight pause after the processor resumes.</summary>
  public void EndPause() {
    lock (_lock) {
      _pauseInFlight = false;
    }
  }
}
