namespace Whizbang.Core.Workers;

/// <summary>
/// Options controlling how the schema initializer runs at startup. The initializer applies
/// migrations and then signals <see cref="ISchemaReadyGate"/> so gated workers can proceed.
/// </summary>
/// <docs>fundamentals/work-coordinator/startup-ordering</docs>
public sealed class SchemaInitializationOptions {
  /// <summary>
  /// When <see langword="false"/> (the default), schema initialization runs <b>inline in the
  /// host's <c>StartAsync</c></b> — the host does not finish starting (no HTTP port bound, no
  /// workers) until migrations complete, and a migration failure aborts startup. This is the safe,
  /// unchanged default for every consumer.
  /// <para>
  /// When <see langword="true"/>, initialization runs <b>in the background</b>: <c>StartAsync</c>
  /// returns immediately so the host binds and can answer a liveness probe, while
  /// <see cref="ISchemaReadyGate"/> stays closed until migrations succeed. Gated workers (and a
  /// readiness health check that reads the gate) hold off until then, so nothing touches an
  /// unmigrated schema. Intended for hosts whose one-time migration is longer than a
  /// k8s startup-probe budget — the pod stays alive and out of traffic rotation instead of being
  /// killed mid-migration. On failure the gate never opens (fail-closed).
  /// </para>
  /// </summary>
  public bool NonBlockingSchemaInit { get; set; }

  /// <summary>
  /// Optional hard ceiling for a single initialization attempt. Only meaningful with
  /// <see cref="NonBlockingSchemaInit"/> = <see langword="true"/>: once <c>StartAsync</c> has
  /// returned and liveness is green, a hung migration (a deadlock or an indefinite lock wait)
  /// would otherwise never be caught. When set and exceeded, initialization is treated as failed
  /// (the gate stays closed) — so the pod never becomes ready and the rollout fails cleanly rather
  /// than sitting alive-but-wedged forever. <see langword="null"/> (the default) = no timeout.
  /// </summary>
  public TimeSpan? MigrationTimeout { get; set; }
}
