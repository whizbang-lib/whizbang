// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Policies;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// A stream's recorded start on the Dapper driver (#2152): answered once the stream no longer holds its first event, and
/// null while it does. Mirrors <c>StreamStartedAtSqlTests.EventStore_GetStreamStartedAt_ReadsTheStreamsStartAsync</c> on
/// the EF Core driver.
/// </summary>
/// <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperPostgresEventStore.cs</code-under-test>
public class DapperStreamStartedAtTests : PostgresTestBase {
  private static readonly DateTime _start = new(2020, 3, 1, 9, 0, 0, DateTimeKind.Utc);

#pragma warning disable CA1859 // Interface typing is the point: the runner holds an IEventStore, and the question is an interface member.
  private IEventStore _store() {
#pragma warning restore CA1859
    var jsonOptions = JsonOptionsHelper.CreateOptions();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      new EventEnvelopeJsonbAdapter(jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null,
      NullLogger<DapperPostgresEventStore>.Instance);
  }

  private async Task _seedAsync(Guid streamId, long lowestVersion) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_stream_lifecycle (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version, first_event_at)
      VALUES (@streamId, 'TestAggregate', NOW(), date_trunc('hour', NOW()), @lowestVersion, @start);
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'TestAggregate', 'TestNamespace.ProbeEvent', @lowestVersion, NOW())",
      new { streamId, lowestVersion, start = _start, eventId = Guid.CreateVersion7() });
  }

  [Test]
  public async Task GetStreamStartedAt_AStreamWithoutItsFirstEvent_IsTheRecordedStartAsync() {
    var streamId = Guid.NewGuid();
    await _seedAsync(streamId, lowestVersion: 2);

    var startedAt = await _store().GetStreamStartedAtAsync(streamId);

    await Assert.That(startedAt).IsEqualTo(_start)
      .Because("the Dapper driver must answer the question, not serve the interface default");
    await Assert.That(startedAt!.Value.Kind).IsEqualTo(DateTimeKind.Utc);
  }

  [Test]
  public async Task GetStreamStartedAt_AStreamHoldingItsFirstEvent_IsNullAsync() {
    var streamId = Guid.NewGuid();
    await _seedAsync(streamId, lowestVersion: 1);

    await Assert.That(await _store().GetStreamStartedAtAsync(streamId)).IsNull();
    await Assert.That(await _store().GetStreamStartedAtAsync(Guid.NewGuid())).IsNull()
      .Because("a stream with no ledger row has no recorded start");
  }
}
