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
/// and commit-sequence lookup: only a save failure whose inner error names a unique violation
/// (by SQLSTATE or by its "duplicate key" text) becomes the concurrent-append error; any other save
/// failure, with or without an inner error, surfaces unchanged. The commit sequence reads back the
/// stamped value, or null for an unknown event.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
[Category("Shard1")]
public class EventStoreAppendFailureTests : EFCoreTestBase {

  [Test]
  public async Task Append_RealDuplicateEventId_IsReportedAsAConcurrentAppendAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.CreateVersion7();
    var envelope = _envelope(Guid.CreateVersion7());
    await store.AppendAsync(streamId, envelope);

    await using var second = CreateDbContext();
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await new EFCoreEventStore<WorkCoordinationDbContext>(second).AppendAsync(streamId, envelope));

    await Assert.That(ex!.Message).Contains("Another process has already appended to this stream");
  }

  // A provider whose unique-violation message carries the words but not the SQLSTATE is still a
  // concurrent append, not an unexplained failure.
  [Test]
  public async Task Append_InnerErrorSaysDuplicateKey_IsReportedAsAConcurrentAppendAsync() {
    var failure = new DbUpdateException("save failed", new InvalidOperationException("Duplicate KEY value violates unique constraint"));

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await _appendThroughAsync(failure));

    await Assert.That(ex!.InnerException).IsSameReferenceAs(failure)
      .Because("the original save failure is preserved for its diagnostics");
  }

  [Test]
  public async Task Append_UnrelatedInnerError_SurfacesUnchangedAsync() {
    var failure = new DbUpdateException("save failed", new InvalidOperationException("connection reset by peer"));

    var ex = await Assert.ThrowsAsync<DbUpdateException>(async () => await _appendThroughAsync(failure));

    await Assert.That(ex).IsSameReferenceAs(failure)
      .Because("a failure that is not a unique violation must not be dressed up as a race");
  }

  [Test]
  public async Task Append_NoInnerError_SurfacesUnchangedAsync() {
    var failure = new DbUpdateException("save failed without an inner error");

    var ex = await Assert.ThrowsAsync<DbUpdateException>(async () => await _appendThroughAsync(failure));

    await Assert.That(ex).IsSameReferenceAs(failure);
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
