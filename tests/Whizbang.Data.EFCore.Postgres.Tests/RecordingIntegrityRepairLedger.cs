// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The in-memory ledger, with every answer-pending mark recorded, so a receptor test can see what a request it sent
/// marked. <see cref="RefuseRepairs"/> makes it deny every grant.
/// </summary>
internal sealed class RecordingIntegrityRepairLedger : IIntegrityRepairLedger {
  public IntegrityRepairLedger Inner { get; } = new();

  public bool RefuseRepairs { get; init; }

  public List<(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> Keys, long? WindowFrom, long? WindowUntil, TimeSpan Timeout)> PendingMarks { get; } = [];

  public ValueTask<bool> TryBeginReportAsync(IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo,
      long localHi, DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
    Inner.TryBeginReportAsync(key, originLo, originHi, localLo, localHi, now, cooldown, cancellationToken);

  public ValueTask<bool> TryBeginRepairAsync(IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff,
      int maxAttempts, CancellationToken cancellationToken = default) =>
    RefuseRepairs ? ValueTask.FromResult(false) : Inner.TryBeginRepairAsync(key, now, baseBackoff, maxAttempts, cancellationToken);

  public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
    Inner.MarkHealedAsync(key, cancellationToken);

  public ValueTask<int> MarkAnswerPendingAsync(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, long? windowFrom,
      long? windowUntil, DateTimeOffset now, TimeSpan timeout, CancellationToken cancellationToken = default) {
    PendingMarks.Add((keys, windowFrom, windowUntil, timeout));
    return Inner.MarkAnswerPendingAsync(keys, windowFrom, windowUntil, now, timeout, cancellationToken);
  }
}
