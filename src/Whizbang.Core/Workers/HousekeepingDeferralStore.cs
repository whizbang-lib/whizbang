// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Carries out the shared-store change a <see cref="HousekeepingCoordinator.Decision"/> reports (#2288): the first
/// deferral of a run, recorded after a refusal, and the record cleared once a granted pass has ended.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator decides under a lock and does no I/O there; the caller that measured the backlog holds the work
/// coordinator, so the caller writes. Every caller of the idle gate goes through here (maintenance, dead-letter recovery,
/// and the integrity requesters through <see cref="IntegrityAdmission"/>), so the record cannot be forgotten by one of
/// them.
/// </para>
/// <para>
/// Best-effort in both directions. A failed write is logged and the pass goes on: the coordinator still measures the
/// ceiling in memory, and the store only ever moves the ceiling earlier, so a lost record costs nothing over the
/// behavior before the store held it. A canceled write is a stopping host and propagates.
/// </para>
/// </remarks>
/// <docs>operations/workers/housekeeping-arbitration#the-ceiling-survives-a-restart</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/HousekeepingDeferralStoreTests.cs</tests>
internal static partial class HousekeepingDeferralStore {
  /// <summary>Records the first deferral the decision reports, if it reports one.</summary>
  /// <param name="coordinator">The work coordinator the backlog was measured through, or null when there is none.</param>
  /// <param name="decision">The decision just made.</param>
  /// <param name="logger">Where a failed write is reported.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the record is written, or at once when there is nothing to write.</returns>
  internal static async ValueTask RecordAsync(
      IWorkCoordinator? coordinator, HousekeepingCoordinator.Decision decision, ILogger logger,
      CancellationToken cancellationToken) {
    if (coordinator is null || decision.DeferralToRecord is not { } record) {
      return;
    }
    try {
      await coordinator.RecordHousekeepingDeferralAsync(record.BudgetKey, record.FirstDeferredAt, cancellationToken)
        .ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogRecordFailed(logger, record.BudgetKey, ex);
    }
  }

  /// <summary>Clears the stored first deferral of a granted pass that has ended, if the decision asks for it.</summary>
  /// <param name="coordinator">The work coordinator the backlog was measured through, or null when there is none.</param>
  /// <param name="decision">The decision that granted the pass.</param>
  /// <param name="logger">Where a failed clear is reported.</param>
  /// <returns>A task that completes when the record is cleared, or at once when there is nothing to clear.</returns>
  /// <remarks>
  /// Takes no cancellation token: it runs where the slot is released, in a <c>finally</c> or a dispose, after a pass
  /// that has already ended, and a stopping host must not leave a completed pass's record behind or replace the pass's
  /// own exception with a cancellation. The coordinator's command timeout bounds it.
  /// </remarks>
  internal static async ValueTask ClearAsync(
      IWorkCoordinator? coordinator, HousekeepingCoordinator.Decision decision, ILogger logger) {
    if (coordinator is null || decision.DeferralToClear is not { } budgetKey) {
      return;
    }
    try {
      await coordinator.ClearHousekeepingDeferralAsync(budgetKey, CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogClearFailed(logger, budgetKey, ex);
    }
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "The first deferral of housekeeping budget {BudgetKey} could not be recorded in the store; the forced-pass "
      + "ceiling is measured in this process until a later deferral records it")]
  private static partial void LogRecordFailed(ILogger logger, string budgetKey, Exception exception);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "The stored first deferral of housekeeping budget {BudgetKey} could not be cleared after its pass; the next "
      + "pass of that activity may be forced sooner than MaxDeferral")]
  private static partial void LogClearFailed(ILogger logger, string budgetKey, Exception exception);
}
