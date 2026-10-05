// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas.Helpers;

/// <summary>
/// The claim-key convention for a saga the completion watchdog abandoned.
/// </summary>
/// <remarks>
/// <para>
/// Convention: <c>"saga-abandoned:{sagaName}:{sagaId}"</c>, beside the completion and continuation keys.
/// </para>
/// <para>
/// The watchdog claims it when it abandons a saga, through <c>PublishOnceAsync</c>, and the
/// stranded-saga sweep leaves a saga holding it alone. That makes the abandonment stick for every
/// consumer, including one whose saga perspective does not apply the abandon event and so never
/// records <see cref="SagaStatus.Abandoned"/>. Without it such a saga was re-armed once per
/// <c>StrandedSagaRearmInterval</c> and published its abandonment again each time, forever.
/// </para>
/// <para>
/// An operator re-drives the saga by releasing the claim, through
/// <c>BaseSagaService.ReDriveAbandonedSagaAsync</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#abandoned-sagas</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_AbandonsUnderTheSagasAbandonmentClaimAsync</tests>
public static class SagaAbandonGuard {

  /// <summary>Builds the abandonment claim key for a saga.</summary>
  /// <param name="sagaName">The saga's name.</param>
  /// <param name="sagaId">The saga's stream id.</param>
  /// <exception cref="ArgumentException"><paramref name="sagaName"/> is blank.</exception>
  public static string ClaimKey(string sagaName, Guid sagaId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
    return $"saga-abandoned:{sagaName}:{sagaId}";
  }
}
