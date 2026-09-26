namespace Whizbang.Sagas.Services;

/// <summary>
/// Tells the stranded-saga sweep which sagas still have a watchdog tick coming.
/// </summary>
/// <remarks>
/// A saga with a tick still waiting (unpublished, scheduled for later, or in an inbox, including one
/// being handled now) has a live chain and must be left alone: waking it would be early, and a second
/// tick would start a second chain that re-arms beside the first forever.
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepStepTests.cs</tests>
public interface ISagaWakeLookup {
  /// <summary>The subset of <paramref name="sagaIds"/> that still have a tick coming.</summary>
  /// <param name="sagaIds">The sagas to check.</param>
  /// <param name="cancellationToken">Cancels the lookup.</param>
  /// <returns>
  /// The sagas with a tick coming, or <see langword="null"/> when this cannot be told; the sweep
  /// then arms nothing, which is the side on which nothing is duplicated.
  /// </returns>
  Task<IReadOnlySet<Guid>?> WithPendingWakeAsync(IReadOnlyList<Guid> sagaIds, CancellationToken cancellationToken);
}
