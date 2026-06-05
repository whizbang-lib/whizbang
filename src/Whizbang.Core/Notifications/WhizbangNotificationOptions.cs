namespace Whizbang.Core.Notifications;

/// <summary>
/// Selects how the C# claim worker is woken when new work arrives.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public enum WorkSignalingMode {
  /// <summary>
  /// Default. Use LISTEN/NOTIFY when a direct connection string can be resolved (via
  /// <see cref="WhizbangNotificationOptions.DirectConnectionString"/> or
  /// <see cref="WhizbangNotificationOptions.ConnectionStringKey"/>); otherwise fall back to
  /// polling-only. Safe default — never throws on misconfig.
  /// </summary>
  Auto,
  /// <summary>
  /// Force polling-only mode. The listener stays disabled regardless of connection-string
  /// configuration. Useful when an environment doesn't permit a separate direct connection
  /// (e.g., pgbouncer-only deployment) or for diagnostic A/B between the two modes.
  /// </summary>
  Polling,
  /// <summary>
  /// Force LISTEN/NOTIFY mode. Throws at startup if no direct connection string can be
  /// resolved. Fail-fast in production where the operator expects burst-latency to be
  /// ≤50ms and a misconfig would silently degrade to ≤250ms polling.
  /// </summary>
  ListenNotify,
}

/// <summary>
/// Configuration for the work-signal notification listener (Phase D).
/// </summary>
/// <remarks>
/// Bind from <c>Whizbang:Database</c> in appsettings or environment variables.
/// Example:
/// <code>
/// {
///   "Whizbang": {
///     "Database": {
///       "ConnectionStringKey": "bffservice-db",
///       "SignalingMode": "Auto"
///     }
///   }
/// }
/// </code>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public sealed class WhizbangNotificationOptions {
  /// <summary>
  /// Polling vs LISTEN/NOTIFY mode. Default <see cref="WorkSignalingMode.Auto"/>.
  /// </summary>
  public WorkSignalingMode SignalingMode { get; set; } = WorkSignalingMode.Auto;

  /// <summary>
  /// IConfiguration <c>ConnectionStrings</c> key whose value is the pooled connection used by
  /// the service's DbContext (e.g., <c>"bffservice-db"</c>). Resolution at startup:
  /// <list type="number">
  ///   <item><description>If <see cref="DirectConnectionString"/> is set, use it directly.</description></item>
  ///   <item><description>Else look up <c>ConnectionStrings:{ConnectionStringKey}-direct</c> — the dedicated direct (pgbouncer-bypass) string for LISTEN-only.</description></item>
  ///   <item><description>Else fall back to <c>ConnectionStrings:{ConnectionStringKey}</c> — the pooled string. Works on direct-Postgres deployments without pgbouncer.</description></item>
  /// </list>
  /// </summary>
  public string? ConnectionStringKey { get; set; }

  /// <summary>
  /// Explicit direct connection string. Overrides <see cref="ConnectionStringKey"/>-based lookup
  /// when set. Mostly used for testing or programmatic overrides.
  /// </summary>
  public string? DirectConnectionString { get; set; }

  /// <summary>
  /// Kill switch — forces polling-only mode even if a connection string can be resolved.
  /// Equivalent to setting <see cref="SignalingMode"/> to <see cref="WorkSignalingMode.Polling"/>;
  /// retained for backward compatibility.
  /// </summary>
  public bool DisableNotifications { get; set; }

  /// <summary>
  /// Safety-net polling cadence used when listener is healthy. Defaults to 30 s — the
  /// maximum tolerable latency before a missed notification is recovered by polling.
  /// </summary>
  public TimeSpan PollingFallbackInterval { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>Cadence of <c>SELECT 1</c> keepalive on the listener connection. Default 30 s.</summary>
  public TimeSpan ListenKeepaliveInterval { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>First reconnect attempt delay after a disconnect. Default 1 s.</summary>
  public TimeSpan ListenReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>Cap on reconnect backoff. Default 30 s.</summary>
  public TimeSpan ListenReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>Exponential growth factor for reconnect backoff. Default 2.0.</summary>
  public double ListenReconnectBackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// Slice 33.2 — round-trip timeout for the gate's self-test probe. After opening the shared
  /// direct connection, the gate <c>LISTEN</c>s a single-use self-test channel, emits a
  /// <c>pg_notify</c> via an ephemeral second connection, and waits up to this duration for
  /// the notification to land. A timeout flips <see cref="INotifySignalingGate.IsAvailable"/>
  /// to <c>false</c> and schedules a reprobe (see <see cref="PeriodicReprobeInterval"/>).
  /// Default 2 s — long enough for any healthy local-Postgres / pgbouncer-direct round-trip,
  /// short enough that misconfigs surface quickly at startup.
  /// </summary>
  public TimeSpan SelfTestTimeout { get; set; } = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Slice 33.2 — cadence at which the gate re-probes while <c>IsAvailable</c> is <c>false</c>.
  /// Default 5 min — covers transient network blips + pgbouncer config errors that get fixed
  /// out-of-band without permanently locking the system into polling-only mode.
  /// </summary>
  public TimeSpan PeriodicReprobeInterval { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// Slice 33.2 — consecutive reconnect / probe failures tolerated before the gate flips
  /// <c>IsAvailable</c> to <c>false</c> and falls back to polling. Producer-side <c>pg_notify</c>
  /// is still emitted (cheap), but consumer-side <c>ClaimWorker</c> stops waiting for signals.
  /// Default 5 — modest tolerance for transient blips before declaring NOTIFY broken for this
  /// process.
  /// </summary>
  public int FailuresBeforeFallback { get; set; } = 5;

  /// <summary>
  /// TCP keepalive idle time in seconds. After this many seconds without traffic, the
  /// OS begins probing the connection's liveness. Default 60 s.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Slice 3 of zero-idle-polling — together with <see cref="TcpKeepAliveInterval"/>, this
  /// is what lets the gate notice silent connection death (NAT/firewall idle eviction)
  /// in roughly <c>TcpKeepAliveTime + 9 × TcpKeepAliveInterval</c> seconds. Defaults
  /// (60 + 9 × 10 = 150 s) sit well inside the 5-minute reprobe interval and far below
  /// the typical 2-hour Linux default that would otherwise leave the gate falsely reporting
  /// <c>IsAvailable=true</c> for hours after the underlying TCP connection died.
  /// </para>
  /// <para>
  /// Npgsql exposes the time/interval pair but not the probe count (Linux default 9).
  /// Tighten the count via OS-level sysctl when you need sub-150 s detection — most
  /// deployments don't.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer#tcp-keepalive</docs>
  public int TcpKeepAliveTime { get; set; } = 60;

  /// <summary>
  /// TCP keepalive probe interval in seconds. Sent every N seconds once the connection
  /// has been idle for <see cref="TcpKeepAliveTime"/>. Default 10 s.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer#tcp-keepalive</docs>
  public int TcpKeepAliveInterval { get; set; } = 10;
}
