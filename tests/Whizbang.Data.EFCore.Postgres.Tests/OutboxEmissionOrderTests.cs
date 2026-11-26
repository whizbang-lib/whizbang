// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A stream's outbox rows are drained in the order they were emitted, never by id (#2551): the store gives each row its
/// emission position, the claim's run, the fetch and the continuation follow it, the continuation's cursor is the last
/// position published, and a failure releases the rest of its run in that order. Each test stores one stream's rows with
/// the later id emitted first, so id order and emission order disagree.
/// </summary>
/// <docs>fundamentals/event-store/version-ordering#dispatch-order</docs>
[Category("Shard5")]
public class OutboxEmissionOrderTests : EFCoreTestBase {
  [Test]
  public async Task AStreamsRows_AreFetchedInEmissionOrder_WithTheirPositions_ThoughTheirIdsSortOtherwiseAsync() {
    await using var ctx = CreateDbContext();
    var (coordinator, instance, stream, emitted) = await _storedAndClaimedAsync(ctx);

    var rows = await coordinator.FetchOutboxBatchAsync([stream], instance, 10, null);

    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo(emitted, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the stream is published in the order its rows were emitted, whatever their ids");
    await Assert.That(rows[0].StreamPosition).IsGreaterThan(0L);
    await Assert.That(rows[1].StreamPosition).IsGreaterThan(rows[0].StreamPosition);
  }

  [Test]
  public async Task TheContinuation_ResumesAfterTheLastPositionPublished_NotTheLargestIdAsync() {
    await using var ctx = CreateDbContext();
    var (coordinator, instance, stream, emitted) = await _storedAndClaimedAsync(ctx);
    var fetched = await coordinator.FetchOutboxBatchAsync([stream], instance, 1, null);

    var rows = await coordinator.ContinueOutboxStreamsAsync(
      [new OutboxStreamCursor(stream, fetched[0].MessageId) { LastPublishedPosition = fetched[0].StreamPosition }],
      instance, runLength: 10, maxBytes: null);

    await Assert.That(fetched.Single().MessageId).IsEqualTo(emitted[0]);
    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo([emitted[1]])
      .Because("the row emitted after the one published is next, though its id sorts before it");
  }

  [Test]
  public async Task AFailure_ReleasesTheRestOfItsRun_InEmissionOrderAsync() {
    await using var ctx = CreateDbContext();
    var (coordinator, _, _, emitted) = await _storedAndClaimedAsync(ctx);

    await coordinator.FlushCompletionsAsync(new FlushCompletionsRequest(FailuresByCategory: [
      new CategoryFailures(WorkCategory.Outbox, [
        new MessageFailure { MessageId = emitted[0], CompletedStatus = MessageProcessingStatus.Stored, Error = "transport down" },
      ]),
    ]));

    await Assert.That(await _holderAsync(ctx, emitted[1])).IsNull()
      .Because("the row emitted after the failed one is released, so the stream is not published past its failure");
  }

  // One stream's two rows, stored in one call with the later id first, then claimed by one instance.
  private static async Task<(EFCoreWorkCoordinator<WorkCoordinationDbContext> Coordinator, Guid Instance, Guid Stream, Guid[] Emitted)>
      _storedAndClaimedAsync(WorkCoordinationDbContext ctx) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    var instance = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    // Time-ordered ids made one after the other: the first sorts first, in the database's byte order as well.
    var lowId = (Guid)TrackedGuid.New();
    var highId = (Guid)TrackedGuid.New();
    Guid[] emitted = [highId, lowId];
    await coordinator.StoreOutboxMessagesAsync([.. emitted.Select(id => CreateTestOutboxMessage(id, "order-topic", stream))], partitionCount: 10000);
    await _executeAsync(ctx, "UPDATE wh_outbox SET instance_id = NULL, lease_expiry = NULL WHERE stream_id = @id", stream);
    await _executeAsync(ctx, "UPDATE wh_active_streams SET assigned_instance_id = NULL, lease_expiry = NULL WHERE stream_id = @id", stream);
    _ = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(instance, "order-svc", "order-host", 1,
      MaxStreams: 1, MaxOutboxAcquireRows: 100, OutboxRunLength: 10));
    return (coordinator, instance, stream, emitted);
  }

  private static async Task<Guid?> _holderAsync(DbContext ctx, Guid messageId) {
    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    return await cmd.ExecuteScalarAsync() as Guid?;
  }

  private static async Task _executeAsync(DbContext ctx, string sql, Guid id) {
    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(id), id);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }
}
