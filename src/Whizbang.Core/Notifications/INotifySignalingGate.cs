namespace Whizbang.Core.Notifications;

/// <summary>
/// Single source of truth for "is Postgres LISTEN/NOTIFY signaling available for this process?".
/// Replaces the per-listener startup decision based on <see cref="WhizbangNotificationOptions"/>:
/// instead of each listener consulting options independently, the gate runs a self-test probe
/// (slice 33.2) and aggregates underlying connection health, then exposes one boolean that all
/// consumers act on.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by <c>PgSharedNotifyConnection</c> (Whizbang.Data.Postgres). The gate is the
/// same singleton that owns the shared direct connection (slice 33.1) — collapsing the
/// pre-slice-33 design where three independent listeners each opened their own conn into a
/// single per-pod direct connection that all subscribers share via <see cref="ISharedNotifyConnection"/>.
/// </para>
/// <para>
/// <strong>Availability semantics.</strong> <see cref="IsAvailable"/> is <c>true</c> when the
/// last self-test probe succeeded AND the underlying connection is currently usable. It flips
/// to <c>false</c> on probe timeout, repeated reconnect failures past
/// <see cref="WhizbangNotificationOptions.FailuresBeforeFallback"/>, or explicit
/// <see cref="WorkSignalingMode.Polling"/> configuration. Consumers (e.g. <c>ClaimWorker</c>'s
/// polling cadence) should treat the flip as authoritative — when <c>false</c>, they must
/// assume notifications will not arrive and act accordingly (poll at a tight fallback cadence
/// rather than waiting for signals).
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public interface INotifySignalingGate {
  /// <summary>
  /// True when the gate's most recent probe succeeded AND the shared connection is currently
  /// usable. False when the probe never succeeded, timed out, or the connection has hit the
  /// configured failure threshold.
  /// </summary>
  bool IsAvailable { get; }

  /// <summary>UTC timestamp of the most recent successful self-test, or null if never.</summary>
  DateTimeOffset? LastVerifiedAt { get; }

  /// <summary>UTC timestamp of the most recent failure (probe timeout, reconnect cap, etc.).</summary>
  DateTimeOffset? LastFailureAt { get; }

  /// <summary>Human-readable reason for the most recent failure (timeout, exception message, etc.).</summary>
  string? LastFailureReason { get; }

  /// <summary>
  /// Fires once on every <see cref="IsAvailable"/> transition. Arg is the new value.
  /// Subscribers handle this synchronously — the gate's loop is on the calling stack — so
  /// long-running work in response to transitions must be dispatched via a worker channel.
  /// </summary>
  event Action<bool>? OnAvailabilityChanged;

  /// <summary>
  /// Force an immediate re-probe, bypassing the periodic schedule. Returns the new
  /// <see cref="IsAvailable"/> value. Useful for ops endpoints ("we just fixed the network,
  /// re-probe now") and for deterministic tests.
  /// </summary>
  Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default);
}
