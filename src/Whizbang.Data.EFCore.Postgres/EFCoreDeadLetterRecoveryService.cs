// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EFCore + Npgsql implementation of <see cref="IDeadLetterRecoveryService"/>: the recovery SQL both Postgres drivers
/// share (<c>PostgresDeadLetterRecoveryService</c>, over the functions migration 051 and later define), run on the
/// context's connection. The connection is the context's: a call opens it when it is closed and leaves it open.
/// </summary>
/// <param name="dbContext">The context whose connection the recovery functions run on.</param>
/// <param name="gate">The gate bounding concurrent coordinator calls, when one is registered.</param>
/// <param name="partitionCount">
/// The partition count every arrival is stored with (<see cref="Whizbang.Core.Workers.ClaimWorkerOptions.PartitionCount"/>),
/// which a recovered outbox or perspective row is stored with too as it re-enters through its store (#1722). When
/// absent, the claim's default.
/// </param>
/// <docs>operations/dead-letter-queue/recovery</docs>
public sealed class EFCoreDeadLetterRecoveryService<TDbContext>(
  TDbContext dbContext,
  WorkCoordinatorGate? gate = null,
  int? partitionCount = null
) : IDeadLetterRecoveryService where TDbContext : DbContext {
  private readonly Whizbang.Data.Postgres.PostgresDeadLetterRecoveryService _recovery = _create(
    dbContext ?? throw new ArgumentNullException(nameof(dbContext)),
    gate,
    partitionCount ?? new Whizbang.Core.Workers.ClaimWorkerOptions().PartitionCount);

  // The DLQ functions are created in the service schema (__SCHEMA__.<fn>, migration 051). Callers
  // must schema-qualify them the same way EFCoreWorkCoordinator qualifies its SQL — a bare call only
  // resolves when the connection's search_path happens to include the service schema, which is not
  // guaranteed (e.g. the ECommerce per-service schemas: 42883 "function ... does not exist").
  private static Whizbang.Data.Postgres.PostgresDeadLetterRecoveryService _create(TDbContext context, WorkCoordinatorGate? gate, int partitions) =>
    new(
      async ct => {
        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) {
          await conn.OpenAsync(ct).ConfigureAwait(false);
        }
        return conn;
      },
      ownsConnection: false,
      name => Whizbang.Data.Postgres.PgIdentifier.Qualify(context.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), name),
      gate,
      partitions);

  /// <inheritdoc />
  public Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) =>
    _recovery.FetchDueAsync(maxCount, ct);

  /// <inheritdoc />
  public Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) => _recovery.PurgeUndeliverableHeldAsync(ct);

  /// <inheritdoc />
  public Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) => _recovery.ListHeldCohortsAsync(ct);

  /// <inheritdoc />
  public Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) =>
    _recovery.BeginCanaryProbesAsync(fingerprint, generation, probeSize, generationBudget, ct);

  /// <inheritdoc />
  public Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) =>
    _recovery.EvaluateCampaignAsync(fingerprint, generation, ct);

  /// <inheritdoc />
  public Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) =>
    _recovery.ReleaseHeldCohortAsync(fingerprint, stagger, ct);

  /// <inheritdoc />
  public Task<IReadOnlyList<string>> GetPassedCampaignFingerprintsAsync(string generation, CancellationToken ct = default) =>
    _recovery.GetPassedCampaignFingerprintsAsync(generation, ct);

  /// <inheritdoc />
  public Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) =>
    _recovery.FetchUnstackedAsync(maxCount, ct);

  /// <inheritdoc />
  public Task<int> RecordStacksAsync(
      IReadOnlyList<(Guid DeadLetterId, Whizbang.Core.DeadLetters.StackIdentity Stack)> entries,
      CancellationToken ct = default) =>
    _recovery.RecordStacksAsync(entries, ct);

  /// <inheritdoc />
  public Task<int> PruneStackHistoryAsync(int retentionDays, CancellationToken ct = default) =>
    _recovery.PruneStackHistoryAsync(retentionDays, ct);

  /// <inheritdoc />
  public Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) =>
    _recovery.RecordStackAsync(deadLetterId, stack, ct);

  /// <inheritdoc />
  public Task<int> BeginTrickleWaveAsync(string fingerprint, string generation, int waveSize, CancellationToken ct = default) =>
    _recovery.BeginTrickleWaveAsync(fingerprint, generation, waveSize, ct);

  /// <inheritdoc />
  public Task<int> CountWaveRequarantinesAsync(string fingerprint, string generation, CancellationToken ct = default) =>
    _recovery.CountWaveRequarantinesAsync(fingerprint, generation, ct);

  /// <inheritdoc />
  /// <remarks>
  /// An outbox or perspective dead letter re-enters through its store, stored with this service's partition count
  /// (#1722); the store claims the dead letter, and the completion settles it.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredOutboxDeadLetterPublishTests.cs:ARecoveredOutboxEvent_IsPublishedAsTheEventItWasStoredAs_AndReadsRecoveredOncePublishedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredPerspectiveDeadLetterApplyTests.cs:ARecoveredPerspectiveWorkRow_IsApplied_AndReadsRecoveredOnceAppliedAsync</tests>
  public Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) => _recovery.RecoverAsync(deadLetterId, ct);

  /// <inheritdoc />
  public Task<DeadLetterRedelivery?> ReadRedeliveryAsync(Guid deadLetterId, CancellationToken ct = default) =>
    _recovery.ReadRedeliveryAsync(deadLetterId, ct);

  /// <inheritdoc />
  public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) => _recovery.MarkHoldingAsync(deadLetterId, ct);

  /// <inheritdoc />
  public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) =>
    _recovery.MarkPermanentlyFailedAsync(deadLetterId, ct);

  /// <inheritdoc />
  public Task MarkDiscardedAsync(Guid deadLetterId, string note, CancellationToken ct = default) =>
    _recovery.MarkDiscardedAsync(deadLetterId, note, ct);

  /// <inheritdoc />
  public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) =>
    _recovery.ScheduleNextAttemptAsync(deadLetterId, nextAt, ct);

  /// <inheritdoc />
  public Task<int> ResetForGenerationAsync(string currentGeneration, int staggerMinutes, CancellationToken ct = default) =>
    _recovery.ResetForGenerationAsync(currentGeneration, staggerMinutes, ct);
}
