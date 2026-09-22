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
  /// <summary>True when a notification transport supplies this gate. Distinct from <see cref="IsAvailable"/>: a
  /// configured gate that reports unavailable means the transport is broken and callers fall back to fast
  /// polling; the framework's null default is simply not configured and callers keep the normal cadence.</summary>
  bool IsConfigured => true;

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

/// <summary>
/// The gate registered when no notification driver is present: signaling is reported as
/// unavailable, so every consumer takes its polling path. This is exactly what a null gate meant
/// before the dependency became required, made explicit and given a reason an operator can read.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public sealed class NullNotifySignalingGate : INotifySignalingGate, INullDefault {
  /// <inheritdoc />
  public bool IsConfigured => false;

  /// <summary>The shared instance; the type carries no state.</summary>
  public static NullNotifySignalingGate Instance { get; } = new();
  private NullNotifySignalingGate() { }
  /// <inheritdoc />
  public bool IsAvailable => false;
  /// <inheritdoc />
  public DateTimeOffset? LastVerifiedAt => null;
  /// <inheritdoc />
  public DateTimeOffset? LastFailureAt => null;
  /// <inheritdoc />
  public string? LastFailureReason => "No notification driver is registered; work is polled rather than signaled.";
  /// <inheritdoc />
  public event Action<bool>? OnAvailabilityChanged { add { /* the null default has no listeners to notify */ } remove { /* the null default has no listeners to notify */ } }
  /// <inheritdoc />
  public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
