// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What the handled-ledger record (#2273, migration 397) adds to the inbox completion, a hot path: 500 stored inbox
/// events are completed in one <c>process_inbox_completions</c> call under <c>EXPLAIN (ANALYZE, BUFFERS)</c>, and the
/// ledger's share of it (the same 500 records written alone, against a ledger already holding 20,000) is measured the
/// same way. Both are reported; the gate is the ledger write's blocks per record, which must stay a primary-key insert
/// (a heap tuple and an index entry, nothing read).
/// </summary>
/// <remarks>
/// Measured (2026-11-11, build server): completing 500 events, 10,397 blocks and 43.0 ms; the 500 ledger records alone,
/// 3,008 blocks (6.0 per record) and 4.1 ms, so the record is about a tenth of the completion it rides in.
/// </remarks>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
[Category("Benchmark")]
[NotInParallel] // A measurement: runs alone so it measures the code, not its neighbors.
[Category("Performance")]
public class HandledLedgerWriteCostScenarioTests : EFCoreTestBase {
  private const int EVENTS = 500;
  private const int PRIOR_RECORDS = 20_000;
  private const string EVENT_TYPE = "Perf.Ledger.SomethingHappened, Perf.Ledger";

  private static InboxMessage _message(Guid id) => new() {
    MessageId = id,
    HandlerName = "SomethingHappenedHandler",
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(id),
      Payload = JsonElement.Parse("{}"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Outbox },
    },
    EnvelopeType = EnvelopeTypeNameHelper.Format(EVENT_TYPE),
    StreamId = TrackedGuid.New().Value,
    IsEvent = true,
    Flags = EventFlags.None,
    Metadata = new EnvelopeMetadata { MessageId = new MessageId(id), Hops = [] },
    MessageType = EVENT_TYPE,
  };

  private static async Task<(long Blocks, double Milliseconds)> _explainAsync(NpgsqlConnection conn, string sql, NpgsqlParameter parameter) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql;
    cmd.Parameters.Add(parameter);
    var json = (string)(await cmd.ExecuteScalarAsync())!;
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement[0];
    var plan = root.GetProperty("Plan");
    var blocks = plan.GetProperty("Shared Hit Blocks").GetInt64() + plan.GetProperty("Shared Read Blocks").GetInt64();
    return (blocks, root.GetProperty("Execution Time").GetDouble());
  }

  [Test]
  [Timeout(600000)]
  public async Task TheHandledRecord_IsAPrimaryKeyInsert_InsideTheCompletionAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using (var prior = conn.CreateCommand()) {
      prior.CommandText = "INSERT INTO wh_receptor_handled (event_id) SELECT unnest($1)";
      prior.Parameters.Add(new NpgsqlParameter { Value = Enumerable.Range(0, PRIOR_RECORDS).Select(_ => TrackedGuid.New().Value).ToArray() });
      await prior.ExecuteNonQueryAsync(cancellationToken);
    }

    var completed = Enumerable.Range(0, EVENTS).Select(_ => TrackedGuid.New().Value).ToArray();
    await using (var ctx = CreateDbContext()) {
      await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions())
        .StoreInboxMessagesAsync([.. completed.Select(_message)], partitionCount: 100, cancellationToken);
    }
    var completions = "[" + string.Join(',', completed.Select(id => $$"""{"MessageId":"{{id}}","Status":2}""")) + "]";
    var completion = await _explainAsync(conn, "SELECT count(*) FROM process_inbox_completions($1::jsonb, NOW())",
      new NpgsqlParameter { Value = completions, NpgsqlDbType = NpgsqlDbType.Jsonb });

    var alone = Enumerable.Range(0, EVENTS).Select(_ => TrackedGuid.New().Value).ToArray();
    var ledger = await _explainAsync(conn, "INSERT INTO wh_receptor_handled (event_id) SELECT unnest($1) ON CONFLICT DO NOTHING",
      new NpgsqlParameter { Value = alone });

    var perRecord = (double)ledger.Blocks / EVENTS;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
      $"completion of {EVENTS}: {completion.Blocks} blocks, {completion.Milliseconds:F1} ms; " +
      $"ledger write of {EVENTS} alone: {ledger.Blocks} blocks ({perRecord:F1}/record), {ledger.Milliseconds:F1} ms"));

    await using (var count = conn.CreateCommand()) {
      count.CommandText = "SELECT count(*) FROM wh_receptor_handled WHERE event_id = ANY($1)";
      count.Parameters.Add(new NpgsqlParameter { Value = completed });
      await Assert.That((long)(await count.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(EVENTS)
        .Because("the measured completion recorded every event it processed");
    }
    await Assert.That(perRecord).IsLessThan(10.0)
      .Because("a record is one heap tuple and one primary-key entry; reading the ledger on the hot path would show here");
  }
}
