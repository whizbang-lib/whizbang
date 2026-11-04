// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tag-bound coalescing on the Dapper driver, with the contract the EF Core driver keeps: the pending groups and their
/// ages, a group's pending singles oldest first, the fold that stores the composite and completes the singles in one
/// transaction, and the deadline release (#2051).
/// </summary>
/// <remarks>
/// <para>
/// This coordinator inherited the interface's inert defaults for all four, so on this driver a coalesced message was
/// parked with its group and floor and never shipped. Each member now runs the statements in <c>CoalesceSql</c>, which the
/// EF Core coordinator runs too.
/// </para>
/// <para>
/// Twin on the EF Core driver: <c>CoalesceFoldCoordinatorSqlTests</c>. Design choice documented at
/// data/drivers#dapper-coordinator-parity.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.Coalesce.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoalesceSql.cs</code-under-test>
[Category("Integration")]
public class DapperCoalesceTests : PostgresTestBase {
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the interface is what the ship worker calls through, so an unimplemented member reaches its default here as it does there.")]
  private IWorkCoordinator _coordinator() =>
    new DapperWorkCoordinator(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task PendingGroups_AreCountedAndAged_AndASinglesFetchIsOldestFirstUpToTheLimitAsync() {
    var coordinator = _coordinator();
    var oldest = await _seedPendingAsync("group-a", createdAgoSeconds: 300);
    var middle = await _seedPendingAsync("group-a", createdAgoSeconds: 200);
    var newest = await _seedPendingAsync("group-a", createdAgoSeconds: 100);
    await _seedPendingAsync("group-b", createdAgoSeconds: 50);
    var processed = await _seedPendingAsync("group-a", createdAgoSeconds: 500);
    await _executeAsync("UPDATE wh_outbox SET processed_at = NOW() WHERE message_id = @processed", new { processed });

    var stats = (await coordinator.GetPendingCoalesceGroupStatsAsync()).ToDictionary(s => s.Group);
    var fetched = await coordinator.FetchPendingCoalesceAsync("group-a", limit: 2);

    await Assert.That(stats.Count).IsEqualTo(2);
    await Assert.That(stats["group-a"].PendingCount).IsEqualTo(3L).Because("a processed single is not pending");
    await Assert.That(stats["group-b"].PendingCount).IsEqualTo(1L);
    await Assert.That(stats["group-a"].OldestCreatedAt).IsLessThan(stats["group-a"].NewestCreatedAt);
    await Assert.That(fetched.Select(f => f.MessageId)).IsEquivalentTo([oldest, middle]);
    await Assert.That(fetched.Select(f => f.MessageId)).DoesNotContain(newest);
    await Assert.That(fetched[0].MessageId).IsEqualTo(oldest);
    await Assert.That(fetched[0].CoalesceGroup).IsEqualTo("group-a");
    await Assert.That(fetched[0].Envelope.Payload.GetProperty("record").GetString()).IsEqualTo("data");
  }

  [Test]
  public async Task Fetch_SynthesizesMinimalMetadataForAJsonNull_AndNamesARowWithNoEnvelopeAsync() {
    var coordinator = _coordinator();
    var minimal = await _seedPendingAsync("group-null-meta", createdAgoSeconds: 10, metadataJson: "null");
    var unreadable = await _seedPendingAsync("group-null-envelope", createdAgoSeconds: 10, eventDataJson: "null");

    var fetched = await coordinator.FetchPendingCoalesceAsync("group-null-meta", limit: 10);
    var thrown = await Assert.That(async () => await coordinator.FetchPendingCoalesceAsync("group-null-envelope", limit: 10))
      .Throws<InvalidOperationException>();

    await Assert.That(fetched[0].Metadata.MessageId.Value).IsEqualTo(minimal);
    await Assert.That(fetched[0].Metadata.Hops).IsEmpty();
    await Assert.That(thrown!.Message).Contains(unreadable.ToString());
  }

  [Test]
  public async Task CompleteFold_StoresTheCompositeAndCompletesTheSinglesTogetherAsync() {
    var coordinator = _coordinator();
    var a = await _seedPendingAsync("group-fold", createdAgoSeconds: 30);
    var b = await _seedPendingAsync("group-fold", createdAgoSeconds: 20);
    var composite = _composite();

    await coordinator.CompleteCoalesceFoldAsync([], [composite], partitionCount: 100);
    await coordinator.CompleteCoalesceFoldAsync([a, b], [composite], partitionCount: 100);

    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids) AND processed_at IS NOT NULL", new { ids = new[] { a, b } }))
      .IsEqualTo(2L).Because("the folded singles complete in the transaction that stores the composite");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_outbox WHERE message_id = @id AND processed_at IS NULL", new { id = composite.MessageId }))
      .IsEqualTo(1L).Because("the composite is stored pending, to ship");
  }

  [Test]
  public async Task ReleaseMatured_ShipsOnlyTheGroupsSinglesPastTheirFloorIndividuallyAsync() {
    var coordinator = _coordinator();
    var matured = await _seedPendingAsync("group-r", createdAgoSeconds: 100, scheduledForSql: "NOW() - INTERVAL '1 second'");
    var waiting = await _seedPendingAsync("group-r", createdAgoSeconds: 10);

    var released = await coordinator.ReleaseMaturedCoalesceAsync("group-r");

    await Assert.That(released).IsEqualTo(1);
    await Assert.That(await _scalarAsync<bool>("SELECT coalesce_group IS NULL AND scheduled_for IS NULL FROM wh_outbox WHERE message_id = @matured", new { matured }))
      .IsTrue();
    await Assert.That(await _scalarAsync<string>("SELECT coalesce_group FROM wh_outbox WHERE message_id = @waiting", new { waiting }))
      .IsEqualTo("group-r").Because("a single whose floor has not passed stays with its group");
  }

  private static OutboxMessage _composite() {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { record = "composite" }),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = "TestEnvelopeType",
      StreamId = Guid.NewGuid(),
      IsEvent = false,
      MessageType = "Whizbang.Tests.CoalescedComposite"
    };
  }

  private async Task<Guid> _seedPendingAsync(
      string group, int createdAgoSeconds, string? scheduledForSql = null, string? eventDataJson = null, string metadataJson = "{}") {
    var messageId = Guid.NewGuid();
    await _executeAsync($@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, coalesce_group, scheduled_for)
      VALUES (@messageId, 'test-topic', 'TestEvent', @data::jsonb, @meta::jsonb, 0, 0,
        NOW() - make_interval(secs => @createdAgoSeconds), gen_random_uuid(), 0, @group,
        {scheduledForSql ?? "NOW() + INTERVAL '60 seconds'"})",
      new {
        messageId, group, createdAgoSeconds,
        data = eventDataJson ?? $"{{\"id\":\"{messageId}\",\"p\":{{\"record\":\"data\"}},\"h\":[]}}",
        meta = metadataJson
      });
    return messageId;
  }

  private async Task _executeAsync(string sql, object? parameters = null) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.ExecuteAsync(sql, parameters);
  }

  private async Task<T> _scalarAsync<T>(string sql, object? parameters = null) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
  }
}
