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
/// <docs>fundamentals/sagas/completion-orchestration</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaServiceCollectionExtensionsTests.cs:AddWhizbangSagas_RegistersSagaOptionsAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_AbandonsAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:NextDelay_ClampedAtMaxAsync</tests>
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
  /// Floor for the watchdog tick's next-fire delay. The adaptive scheduler
  /// in <c>BaseSagaService.TryRecoverViaWatchdogTickAsync</c> clamps the
  /// computed delay against this minimum so a very high observed completion
  /// rate (e.g. a burst of in-flight items completing between ticks) can't
  /// trigger a tight re-arm loop.
  /// </summary>
  public TimeSpan MinWatchdogDelay { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Ceiling for the watchdog tick's next-fire delay. The adaptive scheduler
  /// clamps the computed delay against this maximum so a stalled saga still
  /// gets re-checked on a finite cadence and a near-zero completion rate
  /// (one trailing item taking minutes) doesn't push the next tick far into
  /// the future.
  /// </summary>
  public TimeSpan MaxWatchdogDelay { get; set; } = TimeSpan.FromMinutes(30);

  /// <summary>
  /// Extra slack added on top of the ETA-based next-tick delay when progress
  /// has been observed since the previous tick. The ETA is computed as
  /// <c>remaining_items / observed_rate</c>; without this margin the tick
  /// would fire at the projected completion moment and almost always still
  /// see one or two items in flight. Defaults to 30s — short enough that
  /// stuck sagas surface quickly, long enough that healthy sagas finish by
  /// the next tick.
  /// </summary>
  public TimeSpan WatchdogSafetyMargin { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Number of consecutive ticks observing zero progress (no new terminal
  /// events since the previous tick) before the framework abandons the saga
  /// and emits <see cref="SagaCompletionAbandonedEvent"/>. Progress between
  /// ticks resets the counter — slow sagas don't trigger abandon, only stuck
  /// ones do. Defaults to 4 stalls; combined with the multiplier the abandon
  /// horizon lands ~30 minutes past the last observed progress.
  /// </summary>
  public int MaxConsecutiveStalls { get; set; } = 4;

  /// <summary>
  /// Exponential factor applied to the next-tick delay when the previous
  /// tick observed zero progress. Each stalled tick widens the next interval
  /// by <c>MinWatchdogDelay * Multiplier^stallCount</c>, then the result is
  /// clamped against <see cref="MaxWatchdogDelay"/>. Defaults to 2 — doubling
  /// (30s → 60s → 120s → 240s → 480s) before the ceiling kicks in.
  /// </summary>
  public double StallBackoffMultiplier { get; set; } = 2.0;
}
