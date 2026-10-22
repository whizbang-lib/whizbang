// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas.Helpers;

/// <summary>
/// The claim-key convention for a run's outcome signal, so a run is committed or rolled back once and
/// never both.
/// </summary>
/// <remarks>
/// <para>
/// Convention: <c>"saga-outcome:{sagaName}:{sagaId}"</c>.
/// </para>
/// <para>
/// One key for both outcomes. Two keys would let a run be rolled back by a cancel and later committed
/// by a completion that was already in flight, which is the one result a consumer cannot act on.
/// </para>
/// <para>
/// Deliberately not the completion claim key, for the reason <see cref="SagaContinuationGuard"/> is
/// not: every caller that reaches terminal attempts the signal, including the ones that lost the
/// completion claim, so a process that dies between the completion and the signal does not lose it.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/commit-rollback#exactly-one-outcome</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaOutcomeTests.cs:TheOutcomeClaimKeyIsItsOwnAndSharedByBothOutcomesAsync</tests>
public static class SagaOutcomeGuard {

  /// <summary>Builds the outcome claim key for the dispatcher.</summary>
  /// <param name="sagaName">The name of the saga the run belongs to.</param>
  /// <param name="sagaId">The run's stream id.</param>
  /// <exception cref="ArgumentException"><paramref name="sagaName"/> is blank.</exception>
  public static string ClaimKey(string sagaName, Guid sagaId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
    return $"saga-outcome:{sagaName}:{sagaId}";
  }
}
