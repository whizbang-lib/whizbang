// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DeadLetters;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Dispatch;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for the dead-letter services and the claimed-emission store: every gated
/// method runs both without a <see cref="WorkCoordinatorGate"/> and with one (in which case it
/// parks on the gate and releases its slot afterwards); a DbContext whose model maps no
/// <see cref="OutboxRecord"/> resolves the framework functions and tables through the search path;
/// and the "no answer" scalar shapes read as false / zero.
/// </summary>
/// <remarks>
/// Both arms of each decision are exercised inside this one class on purpose: the CI coverage
/// merge keeps the best per-line condition count of any single shard, so an arm taken only by a
/// test in a different shard does not count toward the line.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Dispatch/EFCoreClaimedEmissionStore.cs</code-under-test>
[Category("Shard5")]
public class DeadLetterGateAndModelSchemaTests : EFCoreTestBase {

  private const string FINGERPRINT = "abcdef0123456789";
  private const string GENERATION = "gen/branch-coverage";

  [Test]
  public async Task RecoveryService_EveryMethod_WithoutAGate_RunsDirectlyAsync() {
    await _exerciseEveryMethodAsync(gate: null);
  }

  // A gated method that skipped the gate would let the recovery worker's scans run beside the
  // claim and flush loops with no cap at all, which is the pool exhaustion the gate exists to
  // prevent. Each call is started while the gate's only slot is held: it must still be waiting
  // when control returns, finish once the slot is released, and give the slot back.
  [Test]
  public async Task RecoveryService_EveryMethod_WithAGate_WaitsForTheSlotAndReleasesItAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, NullLogger<WorkCoordinatorGate>.Instance, acquireTimeoutMilliseconds: 0);
    await _exerciseEveryMethodAsync(gate);
  }

  // A DbContext that maps none of the framework's tables (a read-side context sharing the
  // database) has no model schema to qualify with. The calls must then resolve through the
  // connection's search path rather than throw on a missing entity type; here the framework
  // lives in public, so the bare names find the real functions and tables.
  [Test]
  public async Task UnmappedModel_RecoveryStoreAndClaimCalls_ResolveThroughTheSearchPathAsync() {
    await using var ctx = _bareContext();

    var recovery = new EFCoreDeadLetterRecoveryService<BareContext>(ctx);
    await Assert.That(await recovery.RecoverAsync((Guid)TrackedGuid.New())).IsFalse()
      .Because("the unqualified recover_dead_letter must be found and answer for an unknown id");

    var store = new EFCoreDeadLetterStore<BareContext>(ctx);
    var moved = await store.MoveAsync(
      (Guid)TrackedGuid.New(), "wh_inbox", (Guid)TrackedGuid.New(), MessageFailureReason.Unknown,
      null, (Guid)TrackedGuid.New(), GENERATION);
    await Assert.That(moved).IsNull()
      .Because("the source row does not exist, so the unqualified move finds nothing to move");

    var claims = new EFCoreClaimedEmissionStore(ctx);
    var key = $"branch-coverage:{Guid.NewGuid():N}";
    await Assert.That(await claims.TryClaimAsync(key, (Guid)TrackedGuid.New(), CancellationToken.None)).IsTrue()
      .Because("the first claim on a fresh key wins, written to the search-path table");
    await Assert.That(await claims.TryClaimAsync(key, (Guid)TrackedGuid.New(), CancellationToken.None)).IsFalse()
      .Because("the second claim hits the row the first one wrote, proving both used the same table");

    // The mapped model, in the same class, takes the other arm of the same lookups.
    await using var mapped = CreateDbContext();
    var mappedClaims = new EFCoreClaimedEmissionStore(mapped);
    await Assert.That(await mappedClaims.TryClaimAsync(key, (Guid)TrackedGuid.New(), CancellationToken.None)).IsFalse()
      .Because("a mapped model without a schema resolves to the same public table the bare model wrote");
  }

  [Test]
  public async Task ClaimedEmissionStore_NullContext_ThrowsAsync() {
    await Assert.That(() => new EFCoreClaimedEmissionStore(null!)).Throws<ArgumentNullException>();
  }

  // recover_dead_letter answering NULL (a replaced or broken function) must read as "not
  // recovered", never as a cast failure out of the recovery scan; the reset count likewise reads
  // a NULL as zero rows reset.
  [Test]
  public async Task RecoveryService_NullScalarAnswers_ReadAsFalseAndZeroAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using (var replace = conn.CreateCommand()) {
      replace.CommandText = """
        CREATE OR REPLACE FUNCTION recover_dead_letter(p_dead_letter_id UUID)
        RETURNS BOOLEAN AS $$ SELECT NULL::boolean $$ LANGUAGE sql;
        CREATE OR REPLACE FUNCTION reset_dead_letters_for_generation(
          p_current_generation TEXT, p_stagger_minutes INTEGER DEFAULT 0)
        RETURNS INTEGER AS $$ SELECT NULL::integer $$ LANGUAGE sql;
        """;
      await replace.ExecuteNonQueryAsync();
    }
    var svc = new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(ctx);

    await Assert.That(await svc.RecoverAsync((Guid)TrackedGuid.New())).IsFalse();
    await Assert.That(await svc.ResetForGenerationAsync("gen/null", 0)).IsEqualTo(0);
  }

  // A null note is bound as SQL NULL, not as the text "null" or a crash: the discard still
  // settles the row.
  [Test]
  public async Task RecoveryService_MarkDiscardedWithNullNote_SettlesTheRowAsync() {
    await using var ctx = CreateDbContext();
    var dlqId = await _seedDeadLetterAsync(ctx, gate: null);
    var svc = new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(ctx);

    await svc.MarkDiscardedAsync(dlqId, null!);

    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT recovery_status FROM wh_dead_letters WHERE dead_letter_id = @id";
    cmd.Parameters.AddWithValue("id", dlqId);
    var status = (int)(await cmd.ExecuteScalarAsync())!;
    await Assert.That(status).IsEqualTo((int)DeadLetterRecoveryStatus.Recovered);
  }

  // ===== Helpers =====

  private async Task _exerciseEveryMethodAsync(WorkCoordinatorGate? gate) {
    await using var ctx = CreateDbContext();
    var dlqId = await _seedDeadLetterAsync(ctx, gate);
    var svc = new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(ctx, gate);
    var stack = new StackIdentity(["Frame.A()", "Frame.B()"], FINGERPRINT, IsProse: false);

    var due = await _throughGateAsync(gate, () => svc.FetchDueAsync(10));
    await Assert.That(due.Select(d => d.DeadLetterId)).Contains(dlqId)
      .Because("a fresh dead letter with no next attempt time is due immediately");
    await Assert.That(await _throughGateAsync(gate, () => svc.PurgeUndeliverableHeldAsync())).IsEqualTo(0);
    await Assert.That(await _throughGateAsync(gate, () => svc.ListHeldCohortsAsync())).IsEmpty();
    await Assert.That(await _throughGateAsync(gate, () => svc.BeginCanaryProbesAsync(FINGERPRINT, GENERATION, 2, 3))).IsEqualTo(0)
      .Because("no held row carries the fingerprint, so there is nothing to probe");
    var verdict = await _throughGateAsync(gate, () => svc.EvaluateCampaignAsync(FINGERPRINT, GENERATION));
    await Assert.That(verdict.Kind).IsEqualTo(CanaryVerdictKind.Pending);
    await Assert.That(await _throughGateAsync(gate, () => svc.ReleaseHeldCohortAsync(FINGERPRINT, TimeSpan.FromSeconds(30)))).IsEqualTo(0);
    await Assert.That(await _throughGateAsync(gate, () => svc.GetPassedCampaignFingerprintsAsync(GENERATION))).IsEmpty();
    var unstacked = await _throughGateAsync(gate, () => svc.FetchUnstackedAsync(10));
    await Assert.That(unstacked.Select(u => u.DeadLetterId)).Contains(dlqId)
      .Because("the seeded dead letter carries error text and has no stack yet");
    await Assert.That(await _throughGateAsync(gate, () => svc.RecordStacksAsync([(dlqId, stack)]))).IsEqualTo(1)
      .Because("the stack id is new to this database, so the batch reports one new failure mode");
    await Assert.That(await _throughGateAsync(gate, () => svc.PruneStackHistoryAsync(90))).IsEqualTo(0);
    await _throughGateAsync(gate, async () => {
      await svc.RecordStackAsync(dlqId, stack);
      return true;
    });
    await Assert.That(await _throughGateAsync(gate, () => svc.BeginTrickleWaveAsync(FINGERPRINT, GENERATION, 3))).IsEqualTo(0);
    await Assert.That(await _throughGateAsync(gate, () => svc.CountWaveRequarantinesAsync(FINGERPRINT, GENERATION))).IsEqualTo(0);
    await _throughGateAsync(gate, async () => {
      await svc.ScheduleNextAttemptAsync(dlqId, DateTimeOffset.UtcNow.AddHours(1));
      return true;
    });
    await Assert.That(await _throughGateAsync(gate, () => svc.ResetForGenerationAsync("gen/next", 0))).IsGreaterThanOrEqualTo(0);
    await Assert.That(await _throughGateAsync(gate, () => svc.RecoverAsync(dlqId))).IsTrue()
      .Because("the dead letter's source row can be re-created, so recovery succeeds");
    await Assert.That(await _throughGateAsync(gate, () => svc.RecoverAsync((Guid)TrackedGuid.New()))).IsFalse()
      .Because("an unknown dead letter cannot be recovered");
    await _throughGateAsync(gate, async () => {
      await svc.MarkHoldingAsync(dlqId);
      return true;
    });
    await _throughGateAsync(gate, async () => {
      await svc.MarkDiscardedAsync(dlqId, "discarded in the branch-coverage run");
      return true;
    });
  }

  /// <summary>
  /// With a gate: holds its only slot, starts <paramref name="call"/>, asserts the call is still
  /// parked on the gate, then releases the slot and awaits the result; the call's own release is
  /// checked by the gate having no holder afterwards. Without a gate: awaits the call.
  /// </summary>
  private static async Task<T> _throughGateAsync<T>(WorkCoordinatorGate? gate, Func<Task<T>> call) {
    if (gate is null) {
      return await call();
    }
    var held = await gate.AcquireAsync();
    Task<T> pending;
    try {
      pending = call();
      await Assert.That(pending.IsCompleted).IsFalse()
        .Because("with the only slot held, a gated call must park on the gate before touching the database");
    } finally {
      held.Dispose();
    }
    var result = await pending;
    await Assert.That(gate.SnapshotHolders()).IsEmpty()
      .Because("the call must hand its slot back when it finishes");
    return result;
  }

  /// <summary>
  /// Writes an outbox row and dead-letters it through <see cref="EFCoreDeadLetterStore{TDbContext}"/>
  /// (gated when <paramref name="gate"/> is given), returning the id of the dead letter it made.
  /// </summary>
  private static async Task<Guid> _seedDeadLetterAsync(WorkCoordinationDbContext ctx, WorkCoordinatorGate? gate) {
    var conn = await _openAsync(ctx);
    var messageId = (Guid)TrackedGuid.New();
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
           created_at, stream_id, partition_number)
        VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, 11, NOW(), @stream, 0)
        """;
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", (Guid)TrackedGuid.New());
      await ins.ExecuteNonQueryAsync();
    }
    var store = new EFCoreDeadLetterStore<WorkCoordinationDbContext>(ctx, gate);
    var dlqId = (Guid)TrackedGuid.New();
    var moved = await _throughGateAsync(gate, () => store.MoveAsync(
      dlqId, "wh_outbox", messageId, MessageFailureReason.Unknown, "seeded", (Guid)TrackedGuid.New(), GENERATION));
    await Assert.That(moved).IsEqualTo(dlqId)
      .Because("the source row exists, so the move answers with the dead letter it created");
    return dlqId;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private BareContext _bareContext() =>
    new(new DbContextOptionsBuilder<BareContext>().UseNpgsql(ConnectionString).Options);

  /// <summary>A DbContext that maps none of the framework's entities.</summary>
  public sealed class BareContext(DbContextOptions<BareContext> options) : DbContext(options);
}
