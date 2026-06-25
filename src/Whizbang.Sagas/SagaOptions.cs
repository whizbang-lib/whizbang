namespace Whizbang.Sagas;

/// <summary>
/// App-level configuration for <c>Whizbang.Sagas</c>. Applied at
/// container build via <c>AddWhizbangSagas(opts => …)</c>; all sagas in
/// the process pick up the configured values uniformly.
/// </summary>
/// <remarks>
/// <para>
/// The per-item stream namespace is the only setting that <em>must</em>
/// be configured before any saga operation runs — changing it later
/// re-derives every per-item stream id and orphans existing projection
/// rows. Configure once at startup, never at runtime.
/// </para>
/// </remarks>
public sealed class SagaOptions {

  /// <summary>
  /// Namespace UUID used by <see cref="SagaItemStreams.Of(Guid, string)"/>
  /// (the no-namespace-passed overload) and by every framework call that
  /// derives per-item stream ids by default. Fresh consumers leave this
  /// at the Whizbang default; consumers with pre-existing per-item
  /// streams derived from a different namespace set this to their
  /// historical value so existing rows keep resolving.
  /// </summary>
  public Guid PerItemStreamNamespace { get; set; } = SagaItemStreams.DefaultNamespace;

  /// <summary>
  /// Exponential backoff schedule for the watchdog-tick re-arm path. When
  /// <c>BaseSagaService.TryRecoverViaWatchdogTickAsync</c> finds the saga
  /// is still not terminal, it re-arms <see cref="SagaCompletionWatchdogTickEvent"/>
  /// with the delay at index <c>RescheduleCount</c>. Once
  /// <c>RescheduleCount</c> reaches the length of this array the framework
  /// emits <see cref="SagaCompletionAbandonedEvent"/> instead of another
  /// re-arm — the saga is operationally stuck and needs human triage.
  /// </summary>
  /// <remarks>
  /// Defaults to <c>[30s, 2m, 8m, 30m]</c> — four re-arm attempts spanning
  /// ~40 minutes before abandon, biased toward burst-mode load (back off
  /// quickly while items might still be in flight, then widen) rather than
  /// slow-trickle workloads. Consumers with very large fan-outs (10k+ items)
  /// should override with a longer first delay or an extra abandon-tier.
  /// </remarks>
  public TimeSpan[] WatchdogBackoff { get; set; } = [
    TimeSpan.FromSeconds(30),
    TimeSpan.FromMinutes(2),
    TimeSpan.FromMinutes(8),
    TimeSpan.FromMinutes(30),
  ];
}
