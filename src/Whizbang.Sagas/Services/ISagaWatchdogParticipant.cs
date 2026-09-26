namespace Whizbang.Sagas.Services;

/// <summary>
/// A saga service that receives its own completion-watchdog ticks through the framework's router.
/// </summary>
/// <remarks>
/// <para>
/// Every saga started through <c>BaseSagaService</c> arms a watchdog tick — the guaranteed wake-up
/// that completes, re-arms or abandons a saga whose per-item terminal events were lost. A tick is
/// only useful if something receives it. A saga declared with <c>[Saga]</c> gets a generated
/// receiver; a saga service written by hand does not, and before this contract existed its ticks
/// were delivered on time and discarded, leaving a stranded saga with no safety net at all.
/// </para>
/// <para>
/// <c>BaseSagaService</c> implements this, so a hand-written saga needs nothing beyond being
/// registered with <see cref="SagaServiceCollectionExtensions.AddSagaService{TService}"/>, which
/// exposes it to the router. Do not also register a <c>[Saga]</c>-declared saga this way: it already
/// has a receiver, and two receivers would re-arm every tick twice.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#hand-written-sagas</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs</tests>
public interface ISagaWatchdogParticipant {
  /// <summary>The name ticks are addressed to — the saga name the service arms them with.</summary>
  string SagaName { get; }

  /// <summary>Completes, re-arms or abandons the saga the tick belongs to.</summary>
  /// <param name="tick">The delivered tick.</param>
  /// <param name="cancellationToken">Cancels the recovery attempt.</param>
  /// <returns>What the tick led to.</returns>
  Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(
      SagaCompletionWatchdogTickEvent tick,
      CancellationToken cancellationToken);

  /// <summary>
  /// Arms one watchdog tick for each of this saga's incomplete sagas whose chain has ended.
  /// </summary>
  /// <remarks>
  /// Called by the stranded-saga sweep in the maintenance cycle. The default arms nothing, for a
  /// participant that cannot enumerate its sagas; <c>BaseSagaService</c> implements it.
  /// </remarks>
  /// <param name="wakes">Which sagas still have a tick coming.</param>
  /// <param name="cancellationToken">Cancels the sweep.</param>
  /// <returns>How many ticks this call armed.</returns>
  /// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
  Task<int> ArmStrandedSagasAsync(ISagaWakeLookup wakes, CancellationToken cancellationToken)
    => Task.FromResult(0);
}
