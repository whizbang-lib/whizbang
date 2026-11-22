// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2509: every read of the Dapper store that materializes events carries each event's origin
/// (<c>wh_event_store.origin_service_id</c>, <c>origin_commit_sequence</c>) onto the envelope, as the EF Core store does,
/// so a replay on Dapper deals a received stream into its origin's order.
/// </summary>
/// <docs>fundamentals/events/event-store#read-order</docs>
public class DapperEventOriginReadTests : PostgresTestBase {
  private static readonly string[] _readers = [
    "ReadAsync(fromSequence)", "ReadAsync(fromEventId)", "ReadPolymorphicAsync",
    "GetEventsBetweenAsync", "GetEventsBetweenPolymorphicAsync",
  ];

  [Test]
  public async Task EveryReader_CarriesEachEventsOrigin_OntoItsEnvelopeAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    var origin = (Guid)TrackedGuid.New();
    var envelopes = new[] { _envelope(stream), _envelope(stream), _envelope(stream) };
    foreach (var envelope in envelopes) {
      await store.AppendAsync(stream, envelope);
    }
    var ids = envelopes.Select(e => e.MessageId.Value).ToArray();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // The first two received from another service, at origin positions that run backward to arrival; the third local.
    _ = await conn.ExecuteAsync(
      "UPDATE wh_event_store SET origin_service_id = @origin, origin_commit_sequence = @seq WHERE event_id = @id",
      new[] { new { origin, seq = 42L, id = ids[0] }, new { origin, seq = 41L, id = ids[1] } });

    var reads = await _readEverywayAsync(store, stream);

    await Assert.That(reads.Keys).IsEquivalentTo(_readers).Because("every read of the store that materializes events from its rows is checked");
    string[] expected = [$"{ids[0]}|{origin}|42", $"{ids[1]}|{origin}|41", $"{ids[2]}|{Guid.Empty}|0"];
    foreach (var (reader, read) in reads) {
      await Assert.That(read).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Any)
        .Because($"{reader} carries each row's origin service and origin position onto its envelope");
    }
  }

  [Test]
  public async Task Replay_OfAReceivedStreamReadOnDapper_FoldsInItsOriginsOrderAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    var origin = (Guid)TrackedGuid.New();
    var envelopes = new[] { _envelope(stream), _envelope(stream), _envelope(stream) };
    foreach (var envelope in envelopes) {
      await store.AppendAsync(stream, envelope);
    }
    var ids = envelopes.Select(e => e.MessageId.Value).ToArray();
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      // Arrived (and stored) in the order 0, 1, 2; the origin committed them 2, 0, 1.
      _ = await conn.ExecuteAsync(
        "UPDATE wh_event_store SET origin_service_id = @origin, origin_commit_sequence = @seq WHERE event_id = @id",
        new[] { new { origin, seq = 11L, id = ids[0] }, new { origin, seq = 12L, id = ids[1] }, new { origin, seq = 10L, id = ids[2] } });
    }

    var read = new List<MessageEnvelope<IEvent>>();
    await foreach (var e in store.ReadPolymorphicAsync(stream, null, [typeof(DapperMixedStreamProbeNoted)])) {
      read.Add(e);
    }
    var replayed = ReplayEventOrder.Order(read).Select(e => e.MessageId.Value).ToArray();

    await Assert.That(replayed).IsEquivalentTo(new[] { ids[2], ids[0], ids[1] }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a received stream replays in its origin's order on Dapper, as it does on EF Core");
  }

  // ===== Helpers =====

  private static MessageEnvelope<DapperMixedStreamProbeNoted> _envelope(Guid stream) => new() {
    MessageId = MessageId.From(TrackedGuid.New()),
    Payload = new DapperMixedStreamProbeNoted { StreamId = stream },
    Hops = [new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo { ServiceName = "orders", InstanceId = (Guid)TrackedGuid.New(), HostName = "host", ProcessId = 7 },
      Timestamp = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero),
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static string _describe(IMessageEnvelope envelope) =>
    $"{envelope.MessageId.Value}|{envelope.SourceServiceId}|{envelope.SourceCommitSequence}";

  private static async Task<Dictionary<string, List<string>>> _readEverywayAsync(
      DapperPostgresEventStore store, Guid stream) {
    Type[] types = [typeof(DapperMixedStreamProbeNoted)];
    var reads = new Dictionary<string, List<string>> {
      ["ReadAsync(fromSequence)"] = [],
      ["ReadAsync(fromEventId)"] = [],
      ["ReadPolymorphicAsync"] = [],
    };
    await foreach (var e in store.ReadAsync<DapperMixedStreamProbeNoted>(stream, fromSequence: 0)) {
      reads["ReadAsync(fromSequence)"].Add(_describe(e));
    }
    await foreach (var e in store.ReadAsync<DapperMixedStreamProbeNoted>(stream, fromEventId: null)) {
      reads["ReadAsync(fromEventId)"].Add(_describe(e));
    }
    await foreach (var e in store.ReadPolymorphicAsync(stream, null, types)) {
      reads["ReadPolymorphicAsync"].Add(_describe(e));
    }
    reads["GetEventsBetweenAsync"] = [.. (await store.GetEventsBetweenAsync<DapperMixedStreamProbeNoted>(stream, null, Guid.Empty)).Select(e => _describe(e))];
    reads["GetEventsBetweenPolymorphicAsync"] = [.. (await store.GetEventsBetweenPolymorphicAsync(stream, null, Guid.Empty, types)).Select(_describe)];
    return reads;
  }

  private DapperPostgresEventStore _eventStore() {
    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
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
}
