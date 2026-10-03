using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// The durable predecessor link per ordering key (#1003, migration 190). <c>store_outbox_messages</c> links each keyed
/// collective to the one stored before it on its key, in the storing transaction, from a head row every publisher
/// instance shares; the head row's lock holds to commit, so publishers on one key form one chain.
/// </summary>
/// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
[NotInParallel("PostgreSQL")]
[Category("Integration")]
[Category("CollectiveEvents")]
public class CollectiveOrderingHeadSqlTests : PostgresTestBase {
  private const string LINK_TYPE = "Test.Collectives.Flip";

  private readonly JsonSerializerOptions _jsonOptions = JsonContextRegistry.CreateCombinedOptions();

  /// <summary>One publisher instance: its own coordinator, connecting on its own.</summary>
  private DapperWorkCoordinator _publisher() =>
    new(ConnectionString, _jsonOptions, NullLogger<DapperWorkCoordinator>.Instance);

  /// <summary>A collective on <paramref name="key"/> as the dispatcher writes it: marked for a link unless told not to.</summary>
  private static OutboxMessage _collective(Guid key, Guid id, bool marked = true, string payload = """{"orderingKey":"k"}""") => new() {
    MessageId = id,
    Destination = "collectives",
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(id),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
      Hops = [],
      Payload = JsonDocument.Parse(payload).RootElement,
    },
    Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
    EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
    MessageType = "Test.Collectives.Flip, Test",
    StreamId = key,
    CollectiveLinkType = marked ? LINK_TYPE : null,
  };

  /// <summary>Each stored collective on <paramref name="key"/> and the predecessor its stored payload names.</summary>
  private async Task<Dictionary<Guid, (Guid? PredecessorId, string? PredecessorType)>> _linksAsync(Guid key) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT message_id, event_data->'p'->>'predecessorId', event_data->'p'->>'predecessorType'
      FROM wh_outbox WHERE stream_id = @key
      """;
    cmd.Parameters.AddWithValue("key", key);
    var links = new Dictionary<Guid, (Guid?, string?)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      links[reader.GetGuid(0)] = (
        await reader.IsDBNullAsync(1) ? null : Guid.Parse(reader.GetString(1)),
        await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
    }
    return links;
  }

  private async Task<Guid?> _headAsync(Guid key) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT last_event_id FROM wh_collective_ordering_heads WHERE stream_id = @key";
    cmd.Parameters.AddWithValue("key", key);
    return await cmd.ExecuteScalarAsync() is Guid id ? id : null;
  }

  private async Task<(Guid? Id, string? Type)> _linkDirectlyAsync(Guid key, Guid eventId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT predecessor_id, predecessor_type FROM wh_link_collective_predecessor(@key, @id, @type, now())";
    cmd.Parameters.AddWithValue("key", key);
    cmd.Parameters.AddWithValue("id", eventId);
    cmd.Parameters.AddWithValue("type", LINK_TYPE);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (await reader.IsDBNullAsync(0) ? null : reader.GetGuid(0), await reader.IsDBNullAsync(1) ? null : reader.GetString(1));
  }

  [Test]
  public async Task Link_FirstOnAKey_HasNoPredecessor_AndBecomesTheHeadAsync() {
    var key = Guid.CreateVersion7();
    var first = Guid.CreateVersion7();

    var predecessor = await _linkDirectlyAsync(key, first);

    await Assert.That(predecessor.Id).IsNull();
    await Assert.That(predecessor.Type).IsNull();
    await Assert.That(await _headAsync(key)).IsEqualTo(first);
  }

  /// <summary>The next collective gets the head as its predecessor; the head itself again gets none.</summary>
  [Test]
  public async Task Link_NextOnAKey_ReturnsThePreviousHeadAsync() {
    var key = Guid.CreateVersion7();
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    await _linkDirectlyAsync(key, first);

    var predecessor = await _linkDirectlyAsync(key, second);
    var self = await _linkDirectlyAsync(key, second);

    await Assert.That(predecessor.Id).IsEqualTo(first);
    await Assert.That(predecessor.Type).IsEqualTo(LINK_TYPE);
    await Assert.That(self.Id).IsNull()
      .Because("a collective is never its own predecessor");
  }

  /// <summary>
  /// Two instances of one publisher take turns on a key. Each links to the other's last collective, because the
  /// head is in the database, not in either process.
  /// </summary>
  [Test]
  public async Task Store_TwoPublisherInstancesAlternating_FormOneChainAsync() {
    var key = Guid.CreateVersion7();
    var instanceA = _publisher();
    var instanceB = _publisher();
    var ids = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();

    for (var i = 0; i < ids.Length; i++) {
      await (i % 2 == 0 ? instanceA : instanceB).StoreOutboxMessagesAsync([_collective(key, ids[i])], partitionCount: 10000);
    }

    var links = await _linksAsync(key);
    await Assert.That(links[ids[0]].PredecessorId).IsNull();
    for (var i = 1; i < ids.Length; i++) {
      await Assert.That(links[ids[i]].PredecessorId).IsEqualTo(ids[i - 1])
        .Because($"collective {i} was stored right after collective {i - 1}, by the other instance");
      await Assert.That(links[ids[i]].PredecessorType).IsEqualTo(LINK_TYPE);
    }
    await Assert.That(await _headAsync(key)).IsEqualTo(ids[^1]);
  }

  /// <summary>
  /// Many publishers store on one key at once. The head row's lock serializes them, so the result is one linear
  /// chain through every collective: one with no predecessor, every other naming a distinct stored collective, and
  /// the head reaching all of them.
  /// </summary>
  [Test]
  public async Task Store_ConcurrentPublishesOnOneKey_FormOneLinearChainAsync() {
    var key = Guid.CreateVersion7();
    var ids = Enumerable.Range(0, 12).Select(_ => Guid.CreateVersion7()).ToArray();

    await Task.WhenAll(ids.Select(id => Task.Run(() => _publisher().StoreOutboxMessagesAsync([_collective(key, id)], partitionCount: 10000))));

    var links = await _linksAsync(key);
    await Assert.That(links.Count).IsEqualTo(ids.Length);
    await Assert.That(links.Values.Count(l => l.PredecessorId is null)).IsEqualTo(1)
      .Because("only the first collective stored on the key has no predecessor; two would be a fork");
    var predecessors = links.Values.Where(l => l.PredecessorId is not null).Select(l => l.PredecessorId!.Value).ToList();
    await Assert.That(predecessors.Distinct().Count()).IsEqualTo(predecessors.Count)
      .Because("no collective is the predecessor of two others");
    await Assert.That(predecessors.All(links.ContainsKey)).IsTrue();

    var walked = new List<Guid>();
    for (Guid? at = await _headAsync(key); at is { } id; at = links[id].PredecessorId) {
      walked.Add(id);
    }
    await Assert.That(walked).IsEquivalentTo(ids)
      .Because("walking back from the head visits every collective once");
  }

  /// <summary>The head is durable: a publisher started after a restart links its first collective to the last one stored before it.</summary>
  [Test]
  public async Task Store_AfterARestart_TheChainContinuesAsync() {
    var key = Guid.CreateVersion7();
    var beforeRestart = Guid.CreateVersion7();
    var afterRestart = Guid.CreateVersion7();
    await _publisher().StoreOutboxMessagesAsync([_collective(key, beforeRestart)], partitionCount: 10000);
    NpgsqlConnection.ClearAllPools();

    await _publisher().StoreOutboxMessagesAsync([_collective(key, afterRestart)], partitionCount: 10000);

    var links = await _linksAsync(key);
    await Assert.That(links[afterRestart].PredecessorId).IsEqualTo(beforeRestart);
  }

  /// <summary>
  /// What is not linked: a message without the mark (an unkeyed collective, or one from a publisher that sends no
  /// mark) is stored as before and leaves the head alone; a republish of a stored collective does not move the head
  /// back; a payload that already names a predecessor keeps it; an envelope with no payload object is stored as is.
  /// </summary>
  [Test]
  public async Task Store_UnmarkedMessagesAndRepublishes_AreNotLinkedAsync() {
    var key = Guid.CreateVersion7();
    var publisher = _publisher();
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    var unmarked = Guid.CreateVersion7();
    var alreadyLinked = Guid.CreateVersion7();
    var noPayloadObject = Guid.CreateVersion7();
    var named = Guid.CreateVersion7();

    await publisher.StoreOutboxMessagesAsync([_collective(key, first)], partitionCount: 10000);
    await publisher.StoreOutboxMessagesAsync([_collective(key, second)], partitionCount: 10000);
    await publisher.StoreOutboxMessagesAsync([_collective(key, first)], partitionCount: 10000);
    var headAfterRepublish = await _headAsync(key);
    await publisher.StoreOutboxMessagesAsync([_collective(key, unmarked, marked: false)], partitionCount: 10000);
    var headAfterUnmarked = await _headAsync(key);
    await publisher.StoreOutboxMessagesAsync(
      [_collective(key, alreadyLinked, payload: $$"""{"predecessorId":"{{named}}","predecessorType":"Named"}""")], partitionCount: 10000);
    await publisher.StoreOutboxMessagesAsync([_collective(key, noPayloadObject, payload: "[]")], partitionCount: 10000);

    var links = await _linksAsync(key);
    await Assert.That(headAfterRepublish).IsEqualTo(second)
      .Because("the republished collective is already stored; linking it again would put it after its successor");
    await Assert.That(headAfterUnmarked).IsEqualTo(second);
    await Assert.That(links[unmarked].PredecessorId).IsNull();
    await Assert.That(links[alreadyLinked].PredecessorId).IsEqualTo(named)
      .Because("a link the payload already carries is kept");
    await Assert.That(links[alreadyLinked].PredecessorType).IsEqualTo("Named");
    await Assert.That(await _headAsync(key)).IsEqualTo(noPayloadObject)
      .Because("the head still moves; only the payload has nowhere to carry the link");
  }

  /// <summary>Maintenance forgets a key idle past the retention window and keeps one in use.</summary>
  [Test]
  public async Task Maintenance_PrunesOnlyHeadsIdlePastTheRetentionAsync() {
    var idle = Guid.CreateVersion7();
    var active = Guid.CreateVersion7();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = """
        INSERT INTO wh_collective_ordering_heads (stream_id, last_event_id, last_event_type, updated_at)
        VALUES (@idle, gen_random_uuid(), 'T', now() - interval '8 days'),
               (@active, gen_random_uuid(), 'T', now() - interval '6 days')
        """;
      seed.Parameters.AddWithValue("idle", idle);
      seed.Parameters.AddWithValue("active", active);
      await seed.ExecuteNonQueryAsync();
    }

    long pruned;
    await using (var maintenance = conn.CreateCommand()) {
      maintenance.CommandText = "SELECT rows_affected FROM perform_maintenance() WHERE task_name = 'prune_collective_ordering_heads'";
      pruned = (long)(await maintenance.ExecuteScalarAsync())!;
    }

    await Assert.That(pruned).IsGreaterThanOrEqualTo(1L);
    await Assert.That(await _headAsync(idle)).IsNull();
    await Assert.That(await _headAsync(active)).IsNotNull();
  }
}
