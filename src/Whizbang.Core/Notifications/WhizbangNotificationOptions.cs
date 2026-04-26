namespace Whizbang.Core.Notifications;

/// <summary>
/// Configuration for the work-signal notification listener (Phase D).
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public sealed class WhizbangNotificationOptions {
  /// <summary>
  /// Direct connection string that bypasses pgbouncer. Required to enable LISTEN-based
  /// notifications since pgbouncer transaction-pooling drops session-state. When unset,
  /// the system binds <see cref="NoOpWorkNotificationListener"/> and runs polling-only.
  /// Convention: append <c>-direct</c> to the existing pooled connection string name
  /// (e.g., <c>ConnectionStrings:bffservice-db</c> → <c>ConnectionStrings:bffservice-db-direct</c>).
  /// </summary>
  public string? DirectConnectionString { get; set; }

  /// <summary>
  /// Kill switch — forces polling-only mode even if <see cref="DirectConnectionString"/>
  /// is configured. Useful for ops to disable notifications without redeploy.
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
}
