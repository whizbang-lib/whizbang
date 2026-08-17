namespace Whizbang.Core.Workers;

/// <summary>
/// Options controlling how the schema initializer runs at startup. The initializer applies
/// migrations and then signals <see cref="ISchemaReadyGate"/> so gated workers can proceed.
/// </summary>
/// <docs>fundamentals/work-coordinator/startup-ordering</docs>
public sealed class SchemaInitializationOptions {
  /// <summary>
  /// When <see langword="true"/> (<b>the default — turnkey</b>), initialization runs <b>in the
  /// background</b>: <c>StartAsync</c> returns immediately so the host binds and can answer a liveness
  /// probe, while <see cref="ISchemaReadyGate"/> stays closed until migrations succeed. Gated workers
  /// (and the managed readiness health that reports "migrating" as ready) hold off until then, so
  /// nothing touches an unmigrated schema and the pod is not rolled back for a long migration. On
  /// failure the gate never opens (fail-closed).
  /// <para>
  /// Opt out by setting <see langword="false"/>: schema initialization then runs <b>inline in the
  /// host's <c>StartAsync</c></b> — the host does not finish starting (no HTTP port bound, no workers)
  /// until migrations complete, and a migration failure aborts startup. Choose this only if code after
  /// <c>host.Run()</c> must assume a fully-migrated schema the instant the host starts.
  /// </para>
  /// </summary>
  public bool NonBlockingSchemaInit { get; set; } = true;

  /// <summary>
  /// Optional hard ceiling for a single initialization attempt. Only meaningful with
  /// <see cref="NonBlockingSchemaInit"/> = <see langword="true"/>: once <c>StartAsync</c> has
  /// returned and liveness is green, a hung migration (a deadlock or an indefinite lock wait)
  /// would otherwise never be caught. When set and exceeded, initialization is treated as failed
  /// (the gate stays closed) — so the pod never becomes ready and the rollout fails cleanly rather
  /// than sitting alive-but-wedged forever. <see langword="null"/> (the default) = no timeout.
  /// </summary>
  public TimeSpan? MigrationTimeout { get; set; }

  /// <summary>
  /// Delay between background initialization attempts when
  /// <see cref="NonBlockingSchemaInit"/> is enabled and an attempt fails (including a
  /// <see cref="MigrationTimeout"/>). The loop is fail-closed WHILE retrying — the schema-ready
  /// gate stays shut, so nothing touches an unmigrated schema — but it never gives up: a
  /// transient environment problem (connection exhaustion, a broken pool) recovers, and a pod
  /// that never re-attempts is a not-ready zombie only a human can fix. Default 30 seconds.
  /// </summary>
  /// <docs>data/turnkey-initialization</docs>
  public TimeSpan InitRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
