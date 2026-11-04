// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Transports;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Recovery and fleet operations on the Dapper driver, with the contract the EF Core driver keeps: the stuck-row
/// sentinels, the release of unprocessed inbox rows and unstarted leases, the service backlog, the broker dead-letter
/// import, and the standby and eviction handshake (#2051).
/// </summary>
/// <remarks>
/// <para>
/// This coordinator inherited the interface's inert defaults for every one of them, so on this driver a stuck row was
/// never reported, a lease the instance gave up stayed held until it expired, the housekeeping gate read the backlog as
/// unmeasured, a broker dead letter was never taken into custody, and a standby request went nowhere. Each member now
/// runs the statement in <c>CoordinatorFleetSql</c>, which the EF Core coordinator runs too.
/// </para>
/// <para>
/// Twins on the EF Core driver: <c>EFCoreFindStuckRowsTests</c>, <c>InboxGracefulReleaseSqlTests</c>,
/// <c>BoundedAcquisitionRewriteSqlTests</c>, <c>CountServiceBacklogSqlTests</c>, <c>BrokerDeadLetterImportSqlTests</c>.
/// Design choice documented at data/drivers#dapper-coordinator-parity.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.FleetAndCatalog.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorFleetSql.cs</code-under-test>
[Category("Integration")]
public class DapperFleetRecoveryTests : PostgresTestBase {
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the interface is what the workers call through, so an unimplemented member reaches its default here as it does there.")]
  private IWorkCoordinator _coordinator() =>
    new DapperWorkCoordinator(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task FindStuckRows_ReportsOutboxAndInboxRowsPastTheAttemptLimitAsync() {
    var coordinator = _coordinator();
    var outboxId = Guid.NewGuid();
    var inboxId = Guid.NewGuid();
    var stream = Guid.NewGuid();
    await _executeAsync(@"
      INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      VALUES (@outboxId, 'topic', 'Consumer.StuckCommand', 'TestEnvelope', '{}', '{}', 1, 992, NOW(), @stream, 0)", new { outboxId, stream });
    await _insertInboxAsync(inboxId, stream, attempts: 15);

    var stuckOutbox = await coordinator.FindStuckOutboxRowsAsync(maxAttempts: 10, limit: 50);
    var stuckInbox = await coordinator.FindStuckInboxRowsAsync(maxAttempts: 10, limit: 50);

    await Assert.That(stuckOutbox.Count).IsEqualTo(1);
    await Assert.That(stuckOutbox[0].MessageId).IsEqualTo(outboxId);
    await Assert.That(stuckOutbox[0].MessageType).IsEqualTo("Consumer.StuckCommand");
    await Assert.That(stuckOutbox[0].StreamId).IsEqualTo(stream);
    await Assert.That(stuckOutbox[0].Attempts).IsEqualTo(992);
    await Assert.That(stuckInbox.Select(r => r.MessageId)).IsEquivalentTo([inboxId]);
  }

  [Test]
  public async Task ReleaseUnprocessedInbox_RefundsTheAttemptAndClearsTheLeaseAsync() {
    var coordinator = _coordinator();
    var instance = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    await _insertInboxAsync(messageId, Guid.NewGuid(), attempts: 1, instance);

    var none = await coordinator.ReleaseUnprocessedInboxAsync(instance, []);
    var released = await coordinator.ReleaseUnprocessedInboxAsync(instance, [messageId]);

    await Assert.That(none).IsEqualTo(0);
    await Assert.That(released).IsEqualTo(1);
    await Assert.That(await _scalarAsync<int>("SELECT attempts FROM wh_inbox_state WHERE message_id = @messageId", new { messageId })).IsEqualTo(0)
      .Because("the worker never dispatched the row, so the optimistic charge is refunded");
    await Assert.That(await _scalarAsync<bool>("SELECT instance_id IS NULL AND lease_expiry IS NULL FROM wh_inbox_state WHERE message_id = @messageId", new { messageId }))
      .IsTrue().Because("a released row is claimable again at once");
  }

  [Test]
  public async Task ReleaseUnstartedLeases_ReturnsTheInstancesUnstartedRowsOfTheStreamsAsync() {
    var coordinator = _coordinator();
    var instance = Guid.NewGuid();
    var inboxStream = Guid.NewGuid();
    var perspectiveStream = Guid.NewGuid();
    await _insertInboxAsync(Guid.NewGuid(), inboxStream, attempts: 1, instance);
    await _executeAsync(@"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @perspectiveStream, 'P', gen_random_uuid(), @instance, NOW() + INTERVAL '5 minutes', 0, 0, 1, NOW())",
      new { perspectiveStream, instance });

    var none = await coordinator.ReleaseUnstartedLeasesAsync(instance, [], []);
    var released = await coordinator.ReleaseUnstartedLeasesAsync(instance, [inboxStream], [perspectiveStream]);

    await Assert.That(none).IsEqualTo(new UnstartedLeaseRelease(0, 0));
    await Assert.That(released).IsEqualTo(new UnstartedLeaseRelease(1, 1));
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_perspective_events WHERE instance_id IS NOT NULL")).IsEqualTo(0L);
  }

  [Test]
  public async Task CountServiceBacklog_CountsClaimableLeasedAndIdleRowsAsync() {
    var coordinator = _coordinator();
    var idle = await coordinator.CountServiceBacklogAsync();
    await _insertInboxAsync(Guid.NewGuid(), Guid.NewGuid(), attempts: 0);
    await _insertInboxAsync(Guid.NewGuid(), Guid.NewGuid(), attempts: 1, Guid.NewGuid());

    var busy = await coordinator.CountServiceBacklogAsync();

    await Assert.That(idle).IsNotNull();
    await Assert.That(idle!.UnprocessedInboxRows).IsEqualTo(0L);
    await Assert.That(busy!.UnprocessedInboxRows).IsEqualTo(2L)
      .Because("both rows are unprocessed and claimable, leased or not");
    await Assert.That(busy.ActiveLeasedRows).IsEqualTo(1L);
    await Assert.That(busy.PendingOutboxRows).IsEqualTo(0L);
  }

  [Test]
  public async Task ImportBrokerDeadLetter_TakesCustodyOnce_AndReportsTheDuplicateAsync() {
    var coordinator = _coordinator();
    var import = new BrokerDeadLetterImport(
      MessageId: Guid.NewGuid(), StreamId: Guid.NewGuid(), MessageType: "Consumer.ImportedEvent", Destination: "orders",
      EnvelopeJson: "{\"Payload\":{}}", BrokerReason: "MaxDeliveryCountExceeded", BrokerDescription: "too many",
      EnqueuedAt: null, DeliveryCount: 10);

    var first = await coordinator.ImportBrokerDeadLetterAsync(import);
    var again = await coordinator.ImportBrokerDeadLetterAsync(import);

    await Assert.That(first).IsTrue();
    await Assert.That(again).IsFalse().Because("custody already exists, so the drainer may settle the duplicate at the broker");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_dead_letters WHERE source_id = @MessageId AND source_table = 'broker'", new { import.MessageId }))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task ImportBrokerDeadLetter_AFailedImport_ThrowsRatherThanReadingAsADuplicateAsync() {
    var coordinator = _coordinator();
    await _executeAsync("ALTER TABLE wh_dead_letters RENAME TO wh_dead_letters_gone");
    var import = new BrokerDeadLetterImport(
      MessageId: Guid.NewGuid(), StreamId: null, MessageType: null, Destination: "orders", EnvelopeJson: "{}",
      BrokerReason: null, BrokerDescription: null, EnqueuedAt: null, DeliveryCount: null);

    var thrown = await Assert.That(async () => await coordinator.ImportBrokerDeadLetterAsync(import)).Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains($"for message {import.MessageId} from orders");
  }

  [Test]
  public async Task Standby_IsRequestedRead_Cleared_AndRefusedToAnEvictedInstanceAsync() {
    var coordinator = _coordinator();
    var requester = Guid.NewGuid();
    var evicted = Guid.NewGuid();

    var requested = await coordinator.RequestStandbyAsync(requester, "2.0.0");
    var standing = await coordinator.GetStandbyRequestAsync();
    var cleared = await coordinator.ClearStandbyRequestAsync(requester);
    var none = await coordinator.GetStandbyRequestAsync();
    await coordinator.EvictInstanceAsync(evicted, requester, "superseded");
    var refused = await coordinator.RequestStandbyAsync(evicted, "2.0.0");

    await Assert.That(requested).IsTrue();
    await Assert.That(standing).IsNotNull();
    await Assert.That(standing!.RequestedBy).IsEqualTo(requester);
    await Assert.That(standing.RequestedVersion).IsEqualTo("2.0.0");
    await Assert.That(standing.RequesterIsLive).IsFalse().Because("the requester has no instance row");
    await Assert.That(cleared).IsTrue();
    await Assert.That(none).IsNull();
    await Assert.That(refused).IsFalse().Because("an evicted instance may not ask the fleet to stand by");
  }

  private Task _insertInboxAsync(Guid messageId, Guid stream, int attempts, Guid? instance = null) =>
    _executeAsync(@"
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
        VALUES (@messageId, 'TestHandler', 'Consumer.SampleEvent', '{}', '{}', NOW(), @stream)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry, failure_reason)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, @attempts, 0, @instance::uuid,
             CASE WHEN @instance::uuid IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END, 99 FROM m",
      new { messageId, stream, attempts, instance });

  private async Task _executeAsync(string sql, object? parameters = null) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.ExecuteAsync(sql, parameters);
  }

  private async Task<T> _scalarAsync<T>(string sql, object? parameters = null) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
  }
}
