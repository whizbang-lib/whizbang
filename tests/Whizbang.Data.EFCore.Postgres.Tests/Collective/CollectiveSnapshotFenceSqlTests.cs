// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// The collective snapshot fence (#1963): what a snapshot of a row with collectives records, read in one statement. The
/// mark is the store's highest commit sequence, and a collective of the row's tenant and the model's collective types is
/// in flight while its sink work is unprocessed or its event is unstamped, or, on the read after the row's write, when it
/// was stamped past the mark the read before it returned. Each rule is locked here: a test fails if it flips.
/// </summary>
/// <docs>fundamentals/perspectives/snapshots#collectives</docs>
[Category("Integration")]
[Category("CollectiveEvents")]
[Category("Shard3")]
public class CollectiveSnapshotFenceSqlTests : EFCoreTestBase {
  private const string GRANTED = "Tests.PermissionsGranted, Tests";
  private const string REVOKED = "Tests.PermissionsRevoked, Tests";
  private const string UNRELATED = "Tests.SomethingElse, Tests";
  private static readonly string[] _modelTypes = [GRANTED, REVOKED];

  [Test]
  public async Task Fence_WithNoCollectiveOfTheRowInFlight_IsQuiet_AndMarksTheStoresHighestCommitSequenceAsync() {
    await _eventAsync(GRANTED, "tenant-a", commitSequence: 10, workProcessed: true);
    await _eventAsync(UNRELATED, "tenant-a", commitSequence: 30, workProcessed: null);

    var reading = await _fence().ReadAsync("tenant-a", _modelTypes, afterSequence: null);

    await Assert.That(reading.CollectiveInFlight).IsFalse()
      .Because("the row's one collective is complete, and the other event is not a collective of the model");
    await Assert.That(reading.CompleteThroughSequence).IsEqualTo(30L)
      .Because("every collective of the row stamped at or below the store's highest sequence is complete, and a later one is stamped above it");
  }

  [Test]
  public async Task Fence_WithACollectiveOfTheRowStillQueued_IsInFlightAsync() {
    await _eventAsync(GRANTED, "tenant-a", commitSequence: 10, workProcessed: true);
    await _eventAsync(REVOKED, "tenant-a", commitSequence: 11, workProcessed: false);

    var reading = await _fence().ReadAsync("tenant-a", _modelTypes, afterSequence: null);

    await Assert.That(reading.CollectiveInFlight).IsTrue()
      .Because("an unprocessed sink row may have applied some of its batches and not others");
  }

  [Test]
  public async Task Fence_WithACollectiveOfTheRowCompleteButUnstamped_IsInFlightAsync() {
    await _eventAsync(GRANTED, "tenant-a", commitSequence: null, workProcessed: true);
    await _eventAsync(UNRELATED, "tenant-a", commitSequence: 5, workProcessed: null);

    var reading = await _fence().ReadAsync("tenant-a", _modelTypes, afterSequence: null);

    await Assert.That(reading.CollectiveInFlight).IsTrue()
      .Because("its stamp will land above the mark, so a replay would fold again a collective the row already holds");
  }

  [Test]
  public async Task Fence_IgnoresAnotherTenantsCollectives_AndCollectivesOfOtherTypesAsync() {
    await _eventAsync(GRANTED, "tenant-b", commitSequence: 10, workProcessed: false);
    await _eventAsync(GRANTED, tenant: null, commitSequence: null, workProcessed: false);
    await _eventAsync(UNRELATED, "tenant-a", commitSequence: null, workProcessed: false);

    var reading = await _fence().ReadAsync("tenant-a", _modelTypes, afterSequence: null);

    await Assert.That(reading.CollectiveInFlight).IsFalse()
      .Because("only the collectives a replay of this row folds can make its snapshot wrong");
  }

  [Test]
  public async Task Fence_OfARowWithNoTenant_SeesOnlyCollectivesWithNoTenantAsync() {
    await _eventAsync(GRANTED, "tenant-a", commitSequence: 10, workProcessed: false);
    var quiet = await _fence().ReadAsync(tenantId: null, _modelTypes, afterSequence: null);
    await _eventAsync(GRANTED, tenant: null, commitSequence: 11, workProcessed: false);

    var inFlight = await _fence().ReadAsync(tenantId: null, _modelTypes, afterSequence: null);

    await Assert.That(quiet.CollectiveInFlight).IsFalse().Because("a tenant's collective never folds into a row with no tenant");
    await Assert.That(inFlight.CollectiveInFlight).IsTrue();
  }

  [Test]
  public async Task Fence_AfterAMark_IsInFlight_WhenACollectiveOfTheRowWasStampedPastItAsync() {
    await _eventAsync(GRANTED, "tenant-a", commitSequence: 10, workProcessed: true);
    var before = await _fence().ReadAsync("tenant-a", _modelTypes, afterSequence: null);
    await _eventAsync(UNRELATED, "tenant-a", commitSequence: 12, workProcessed: null);
    var unrelatedSince = await _fence().ReadAsync("tenant-a", _modelTypes, before.CompleteThroughSequence);
    await _eventAsync(REVOKED, "tenant-a", commitSequence: 13, workProcessed: true);

    var collectiveSince = await _fence().ReadAsync("tenant-a", _modelTypes, before.CompleteThroughSequence);

    await Assert.That(before.CompleteThroughSequence).IsEqualTo(10L);
    await Assert.That(unrelatedSince.CollectiveInFlight).IsFalse()
      .Because("an event that is not a collective of the row does not touch it, however far the store moved");
    await Assert.That(collectiveSince.CollectiveInFlight).IsTrue()
      .Because("a collective that completed between the two reads may have changed the row after the first, so the snapshot cannot say whether it holds it");
  }

  [Test]
  public async Task Fence_OfAnEmptyStore_IsQuiet_WithNoMarkAsync() {
    var reading = await _fence().ReadAsync("tenant-a", _modelTypes, afterSequence: null);

    await Assert.That(reading.CollectiveInFlight).IsFalse();
    await Assert.That(reading.CompleteThroughSequence).IsNull();
  }

  [Test]
  public async Task Fence_RefusesANullConnectionAndANullTypeListAsync() {
    await Assert.That(() => new PostgresCollectiveSnapshotFence(null!)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(async () => await _fence().ReadAsync("tenant-a", null!, afterSequence: null)).ThrowsExactly<ArgumentNullException>();
  }

  // -------------------------------------------------------------------------------------------

  private PostgresCollectiveSnapshotFence _fence() =>
    new(new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null));

  // One event on a stream of its own, with its scope's tenant and its stamp; when workProcessed is given, a collective with
  // a sink work row, processed or not.
  private async Task _eventAsync(string eventType, string? tenant, long? commitSequence, bool? workProcessed) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    await using (var insert = connection.CreateCommand()) {
      insert.CommandText = """
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, scope, flags)
        VALUES (@id, @stream, @stream, 'FenceAggregate', @type, 1, @cs, @scope::jsonb, @flags)
        """;
      insert.Parameters.AddWithValue("id", eventId);
      insert.Parameters.AddWithValue("stream", streamId);
      insert.Parameters.AddWithValue("type", eventType);
      insert.Parameters.Add(new NpgsqlParameter("cs", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)commitSequence ?? DBNull.Value });
      insert.Parameters.AddWithValue("scope", tenant is null ? "{}" : $$"""{"t":"{{tenant}}"}""");
      insert.Parameters.AddWithValue("flags", workProcessed is null ? 0 : 1);
      await insert.ExecuteNonQueryAsync();
    }
    if (workProcessed is not bool processed) {
      return;
    }
    await using var work = connection.CreateCommand();
    work.CommandText = """
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, processed_at)
      VALUES (gen_random_uuid(), @stream, '__collective__', @id, 0, 1, 0, now(), CASE WHEN @processed THEN now() END)
      """;
    work.Parameters.AddWithValue("stream", streamId);
    work.Parameters.AddWithValue("id", eventId);
    work.Parameters.AddWithValue("processed", processed);
    await work.ExecuteNonQueryAsync();
  }
}
