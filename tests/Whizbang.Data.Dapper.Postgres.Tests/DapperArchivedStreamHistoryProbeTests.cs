// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Policies;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #1933 on the Dapper driver: <c>GetLastSequenceAsync</c> and <c>HasStreamEventsBeforeAsync</c> account for the archive
/// the stream lifecycle ledger records, through the same SQL as the EF Core driver. Mirrors
/// <c>ArchivedStreamHistoryProbeSqlTests</c>.
/// </summary>
/// <docs>fundamentals/events/archive-store#version-questions</docs>
public class DapperArchivedStreamHistoryProbeTests : PostgresTestBase {
  private static readonly Type[] _handled = [typeof(HistoryProbeContracts.HandledEvent)];

#pragma warning disable CA1859 // Interface typing is the point: callers hold an IEventStore.
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

  private PostgresTableArchiveStore _archive() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private async Task<(Guid Stream, Guid[] Ids)> _seedAsync(int count) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var stream = Guid.CreateVersion7();
    var ids = new Guid[count];
    for (var i = 0; i < count; i++) {
      ids[i] = Guid.CreateVersion7();
    }
    Array.Sort(ids);
    foreach (var id in ids) {
      var request = $$"""
        {
          "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
          "new_outbox_messages": [{
            "MessageId": "{{id}}", "Destination": "out", "MessageType": "Whizbang.Tests.LedgerEntry", "EnvelopeType": null,
            "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{id}}", "Hops": []},
            "Metadata": null, "Scope": null, "StreamId": "{{stream}}", "IsEvent": true, "Flags": 0
          }]
        }
        """;
      _ = await connection.ExecuteAsync("SELECT commit_handler_result(@Req::jsonb)", new { Req = request });
    }
    return (stream, ids);
  }

  [Test]
  public async Task LastSequence_OfAStreamArchivedWhole_IsTheTombstonesLastVersionAsync() {
    var (stream, _) = await _seedAsync(3);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(stream))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);

    await Assert.That(await _store().GetLastSequenceAsync(stream)).IsEqualTo(3L);
    await Assert.That(await _store().GetLastSequenceAsync(Guid.CreateVersion7())).IsEqualTo(-1L).Because("the control: no stream");
  }

  [Test]
  public async Task Probe_AStreamArchivedThroughAPrefix_HasHistoryBeforeItsFirstHotEvent_AndOneNeverArchivedDoesNotAsync() {
    var (archived, archivedIds) = await _seedAsync(4);
    var (neverArchived, neverIds) = await _seedAsync(2);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(archived, 2))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    var store = _store();

    await Assert.That(await store.HasStreamEventsBeforeAsync(archived, archivedIds[2])).IsTrue();
    await Assert.That(await store.HasStreamEventsBeforeAsync(archived, archivedIds[2], _handled)).IsTrue();
    await Assert.That(await store.HasStreamEventsBeforeAsync(neverArchived, neverIds[0])).IsFalse();
    await Assert.That(await store.HasStreamEventsBeforeAsync(neverArchived, neverIds[0], _handled)).IsFalse();
  }
}
