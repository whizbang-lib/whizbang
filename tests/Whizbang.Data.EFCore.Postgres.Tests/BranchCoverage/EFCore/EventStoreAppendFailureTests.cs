// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="EFCoreEventStore{TDbContext}"/>'s append failure classification
/// and commit-sequence lookup: only a unique violation (SQLSTATE 23505) becomes the duplicate-event
/// error, since the database assigns versions under the stream's lock (#1555); any other database
/// failure, and a failure saving the context's pending changes, surfaces unchanged. The commit
/// sequence reads back the stamped value, or null for an unknown event.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
[Category("Shard1")]
public class EventStoreAppendFailureTests : EFCoreTestBase {

  [Test]
  public async Task Append_RealDuplicateEventId_IsReportedAsADuplicateEvent_NotARaceAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.CreateVersion7();
    var envelope = _envelope(Guid.CreateVersion7());
    await store.AppendAsync(streamId, envelope);

    await using var second = CreateDbContext();
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await new EFCoreEventStore<WorkCoordinationDbContext>(second).AppendAsync(streamId, envelope));

    await Assert.That(ex!.Message).Contains("an event with this id is already stored");
    await Assert.That(ex.Message).DoesNotContain("Concurrent modification")
      .Because("versions are assigned under the stream's lock, so a unique violation is never a race (#1555)");
    await Assert.That((ex.InnerException as PostgresException)?.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
  }

  // Any other database failure is not dressed up as a duplicate.
  [Test]
  public async Task Append_NonUniqueDatabaseError_SurfacesUnchangedAsync() {
    await using (var connection = new NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await using var drop = connection.CreateCommand();
      drop.CommandText = "SELECT drop_all_overloads('wh_append_event')";
      await drop.ExecuteNonQueryAsync();
    }
    await using var context = CreateDbContext();

    var ex = await Assert.ThrowsAsync<PostgresException>(
      async () => await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(Guid.CreateVersion7(), _envelope(Guid.CreateVersion7())));

    await Assert.That(ex!.SqlState).IsEqualTo(PostgresErrorCodes.UndefinedFunction);
  }

  // The append saves the context's pending changes first; when that save fails, it fails as itself.
  [Test]
  public async Task Append_PendingChangesFailToSave_SurfacesUnchangedAsync() {
    var failure = new DbUpdateException("save failed", new InvalidOperationException("connection reset by peer"));

    var ex = await Assert.ThrowsAsync<DbUpdateException>(async () => await _appendThroughAsync(failure));

    await Assert.That(ex).IsSameReferenceAs(failure)
      .Because("a failure that is not the append's own must not be dressed up as one");
  }

  [Test]
  public async Task GetCommitSequence_StampedEvent_ReadsTheStamp_UnknownEvent_ReadsNullAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var eventId = Guid.CreateVersion7();
    await store.AppendAsync(Guid.CreateVersion7(), _envelope(eventId));
    await using (var connection = new NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await using var stamp = connection.CreateCommand();
      stamp.CommandText = "UPDATE wh_event_store SET commit_sequence = 42 WHERE event_id = @id";
      stamp.Parameters.AddWithValue("id", eventId);
      await stamp.ExecuteNonQueryAsync();
    }

    await Assert.That(await store.GetCommitSequenceAsync(eventId)).IsEqualTo(42L);
    await Assert.That(await store.GetCommitSequenceAsync(Guid.CreateVersion7())).IsNull()
      .Because("an event that was never stored has no commit sequence");
  }

  // ===== Helpers =====

  private async Task _appendThroughAsync(DbUpdateException failure) {
    var options = new DbContextOptionsBuilder<WorkCoordinationDbContext>(DbContextOptions)
      .AddInterceptors(new FailingSaveInterceptor(failure))
      .Options;
    await using var context = new WorkCoordinationDbContext(options);
    // A pending change, so the append's flush of the context's tracked changes runs and fails.
    context.Set<MessageDeduplicationRecord>().Add(new MessageDeduplicationRecord { MessageId = Guid.CreateVersion7(), FirstSeenAt = DateTimeOffset.UnixEpoch });
    await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(Guid.CreateVersion7(), _envelope(Guid.CreateVersion7()));
  }

  private static MessageEnvelope<OrderCreatedEvent> _envelope(Guid eventId) => new() {
    MessageId = MessageId.From(eventId),
    Payload = new OrderCreatedEvent { OrderId = Guid.NewGuid(), CustomerName = "Branch" },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTime.UtcNow,
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.NewGuid(),
          ServiceName = "test-service",
          HostName = "test-host",
          ProcessId = 123
        }
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  /// <summary>Fails every save with the given exception, before anything reaches the database.</summary>
  private sealed class FailingSaveInterceptor(DbUpdateException failure) : SaveChangesInterceptor {
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
      ValueTask.FromException<InterceptionResult<int>>(failure);
  }
}
