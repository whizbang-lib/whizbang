// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2625 (V2 of the vector storage plan): the stored body of a vector event holds its vectors packed binary32, written
/// in SQL by every write path (both emit chains and <c>wh_append_event</c>) once the packing gate is open, and never
/// before. The outbox and inbox rows, which the transport and other services read, keep JSON numbers. The packed bits
/// are exactly the bits a C# reader reads from the stored array.
/// </summary>
/// <docs>data/event-store#packed-vectors</docs>
/// <docs>data/event-store#packing-gate</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/720_VectorEventBodiesArePackedOnceEveryInstanceReadsThem.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/VectorPackingGateStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class PackedVectorStorageSqlTests : EFCoreTestBase {
  private const string OUTBOX = "outbox";
  private const string INBOX = "inbox";
  private const string APPEND = "append";
  private const string BODY_SQL = "SELECT event_data::text FROM wh_event_body WHERE event_id = @e";
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private static readonly DateTimeOffset _t0 = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

  // ===== The packing function, against the C# bits =====

  [Test]
  public async Task Pack_WritesTheBitsACSharpReaderReadsFromTheStoredArray_ForRandomAndEdgeValuesAsync() {
    await using var conn = await _openAsync();
    var random = new Random(2625);
    float[] values = [
      0.1f, 1f / 3f, -2.5f, 0f, -0f, float.MaxValue, -float.MaxValue, float.Epsilon, -float.Epsilon, 1.17549435E-38f,
      16777217f, 123456.789f, .. Enumerable.Range(0, 3072).Select(_ => (float)((random.NextDouble() * 2) - 1))];
    var arrayText = JsonSerializer.Serialize(values, _json.GetTypeInfo(typeof(float[])));

    var packed = await _scalarAsync<string>(conn,
      "SELECT _wh_pack_vectors(jsonb_build_object('v', @a::jsonb), '[[\"v\"]]'::jsonb) ->> 'v'", ("a", arrayText));
    var stored = await _scalarAsync<string>(conn, "SELECT (@a::jsonb)::text", ("a", arrayText));
    var readFromStoredArray = JsonSerializer.Deserialize<float[]>(stored, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<float[]>)_json.GetTypeInfo(typeof(float[])))!;
    var unpacked = _decode(packed);

    await Assert.That(unpacked.Length).IsEqualTo(values.Length);
    for (var i = 0; i < values.Length; i++) {
      await Assert.That(BitConverter.SingleToUInt32Bits(unpacked[i])).IsEqualTo(BitConverter.SingleToUInt32Bits(readFromStoredArray[i]))
        .Because($"element {i} ({values[i]:R}) packs to the bits a reader reads from the stored array");
      if (!(values[i] == 0f && float.IsNegative(values[i]))) {
        await Assert.That(BitConverter.SingleToUInt32Bits(unpacked[i])).IsEqualTo(BitConverter.SingleToUInt32Bits(values[i]))
          .Because($"element {i} ({values[i]:R}) packs to the bits the writer held");
      }
    }
    await Assert.That(BitConverter.SingleToUInt32Bits(unpacked[4])).IsEqualTo(0u)
      .Because("jsonb holds a JSON number as numeric, which has no negative zero, so the stored array already read -0 as 0");
    await Assert.That(packed).DoesNotContain("\n").Because("the packed form is base64 without line breaks");
  }

  [Test]
  [Arguments("[]")]
  [Arguments("[null]")]
  [Arguments("[1, null]")]
  [Arguments("[1, \"NaN\"]")]
  [Arguments("[true]")]
  [Arguments("[[1]]")]
  [Arguments("[1e39]")]
  [Arguments("[1e-50]")]
  [Arguments("\"AAAAAA==\"")]
  [Arguments("{\"a\": [1]}")]
  [Arguments("7")]
  public async Task Pack_LeavesAValueThatIsNotAnArrayOfBinary32Numbers_AsItIsAsync(string value) {
    await using var conn = await _openAsync();

    var result = await _scalarAsync<bool>(conn,
      "SELECT _wh_pack_vectors(jsonb_build_object('v', @a::jsonb), '[[\"v\"]]'::jsonb) = jsonb_build_object('v', @a::jsonb)",
      ("a", value));

    await Assert.That(result).IsTrue().Because($"{value} is not a vector a reader would read as binary32 floats");
  }

  [Test]
  public async Task Pack_FollowsEveryPath_NestedAndMissing_AndLeavesTheRestOfTheBodyAsync() {
    await using var conn = await _openAsync();

    var packed = await _scalarAsync<string>(conn, """
      SELECT _wh_pack_vectors('{"a": [1], "n": {"v": [2]}, "keep": [3], "t": "x"}'::jsonb,
                              '[["a"], ["n", "v"], ["missing"], ["t", "deeper"]]'::jsonb)::text
      """);

    await Assert.That(packed).IsEqualTo("""{"a": "P4AAAA==", "n": {"v": "QAAAAA=="}, "t": "x", "keep": [3]}""");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT _wh_pack_vectors(NULL, '[[\"a\"]]'::jsonb) IS NULL")).IsTrue();
    await Assert.That(await _scalarAsync<string>(conn, "SELECT _wh_pack_vectors('{\"a\": [1]}'::jsonb, NULL)::text"))
      .IsEqualTo("""{"a": [1]}""");
  }

  // ===== The write paths =====

  [Test]
  [Arguments(OUTBOX)]
  [Arguments(INBOX)]
  [Arguments(APPEND)]
  public async Task WritePath_StoresJsonNumbersWhileTheGateIsClosed_AndPackedVectorsOnceItIsOpenAsync(string path) {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();
    var services = _services();
    var step = _step();
    // The gate stays closed while an instance of an earlier release is live, so the paths are registered first.
    var earlier = await _registerAnEarlierReleaseAsync(conn);
    await Assert.That(await step.OpenAsync(services, CancellationToken.None)).StartsWith("closed");
    var before = _sample(1);
    var beforeId = await _storeAsync(conn, path, before);

    await _executeAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @i", ("i", earlier));
    await Assert.That(await step.OpenAsync(services, CancellationToken.None)).IsEqualTo(VectorPackingGateStep.OPENED);
    var after = _sample(2);
    var afterId = await _storeAsync(conn, path, after);

    var beforeBody = JsonNode.Parse(await _scalarAsync<string>(conn, BODY_SQL, ("e", beforeId)))!;
    var afterBody = JsonNode.Parse(await _scalarAsync<string>(conn, BODY_SQL, ("e", afterId)))!;
    await Assert.That(beforeBody["Embedding"]).IsTypeOf<JsonArray>().Because("no body is packed before the gate opens");
    await Assert.That(afterBody["Embedding"]!.GetValueKind()).IsEqualTo(JsonValueKind.String);
    await Assert.That(afterBody["Lead"]!["Embedding"]!.GetValueKind()).IsEqualTo(JsonValueKind.String).Because("a nested path packs too");
    await Assert.That(afterBody["Centroid"]).IsTypeOf<JsonArray>().Because("an opted-out vector is never packed");
    await Assert.That(afterBody["Weights"]).IsTypeOf<JsonArray>().Because("doubles are never packed");
    await Assert.That(afterBody["Text"]!.GetValue<string>()).IsEqualTo(after.Text);
    await _assertReadsBackAsync(afterBody.ToJsonString(), after);
    await _assertReadsBackAsync(beforeBody.ToJsonString(), before);
  }

  [Test]
  [Arguments(OUTBOX)]
  [Arguments(INBOX)]
  public async Task WirePath_TheOutboxAndInboxRowsKeepJsonNumbers_WhileTheStoredBodyIsPackedAsync(string path) {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    var sample = _sample(3);

    var id = await _storeAsync(conn, path, sample);

    var stored = JsonNode.Parse(await _scalarAsync<string>(conn, BODY_SQL, ("e", id)))!;
    await Assert.That(stored["Embedding"]!.GetValueKind()).IsEqualTo(JsonValueKind.String).Because("control: the stored body is packed");
    var wire = path == OUTBOX
      ? await _scalarAsync<string>(conn, "SELECT event_data::text FROM wh_outbox WHERE message_id = @e", ("e", id))
      : await _scalarAsync<string>(conn, "SELECT event_data::text FROM wh_inbox WHERE message_id = @e", ("e", id));
    var payload = JsonNode.Parse(wire)!;
    payload = payload["p"] ?? payload["Payload"] ?? payload;
    await Assert.That(payload["Embedding"]).IsTypeOf<JsonArray>().Because("the row the transport reads keeps the wire form");
    await Assert.That(payload["Lead"]!["Embedding"]).IsTypeOf<JsonArray>();
  }

  [Test]
  public async Task WritePath_AnEventOfATypeWithNoRegisteredPath_IsStoredAsWrittenAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    var id = (Guid)TrackedGuid.New();
    const string BODY = """{"Embedding": [1, 2], "n": 1}""";

    await _executeAsync(conn, """
      SELECT wh_append_event(@id, @sid, @sid, 'Unregistered', 'Some.Unregistered, Elsewhere', @body::jsonb, '{}'::jsonb, NULL, @at, 0)
      """, ("id", id), ("sid", (Guid)TrackedGuid.New()), ("body", BODY), ("at", _t0));

    await Assert.That(await _scalarAsync<string>(conn, BODY_SQL, ("e", id))).IsEqualTo("""{"n": 1, "Embedding": [1, 2]}""");
  }

  // ===== What an ordinary event costs =====

  [Test]
  public async Task EmitChain_AnOrdinaryEvent_NeverRunsThePacking_EvenWithTheGateOpen_AndAVectorEventDoesAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    var notices = new List<string>();
    conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);
    await _executeAsync(conn, """
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_level = 'notice';
      """);

    _ = await _storeAsync(conn, OUTBOX, new PackedVectorOrdinaryEvent { PassageId = (Guid)TrackedGuid.New(), Amount = 1 });
    var ordinary = notices.Count(n => n.Contains(PACK_STATEMENT, StringComparison.Ordinal));
    notices.Clear();
    _ = await _storeAsync(conn, OUTBOX, _sample(4));
    var vector = notices.Count(n => n.Contains(PACK_STATEMENT, StringComparison.Ordinal));

    await Assert.That(ordinary).IsEqualTo(0).Because("an event whose type has no vector path runs nothing new");
    await Assert.That(vector).IsGreaterThan(0).Because("control: the marker names the packing statement");
  }

  /// <summary>Text only the statement that packs one vector holds.</summary>
  private const string PACK_STATEMENT = "float4send";

  // ===== Helpers =====

  private static PackedVectorSampleEvent _sample(int seed) {
    var random = new Random(seed);
    float[] vector(int n) => [.. Enumerable.Range(0, n).Select(_ => (float)random.NextDouble())];
    return new PackedVectorSampleEvent {
      PassageId = (Guid)TrackedGuid.New(),
      Text = $"passage {seed}",
      Embedding = vector(64),
      Lead = new PackedVectorChunk("lead", vector(8)),
      Centroid = vector(4),
      Weights = [0.1, 0.2],
    };
  }

  private static float[] _decode(string packed) {
    var bytes = Convert.FromBase64String(packed);
    var values = new float[bytes.Length / sizeof(float)];
    for (var i = 0; i < values.Length; i++) {
      values[i] = BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i * sizeof(float)));
    }
    return values;
  }

  private static async Task _assertReadsBackAsync(string body, PackedVectorSampleEvent expected) {
    var read = (PackedVectorSampleEvent)JsonSerializer.Deserialize(body, _json.GetTypeInfo(typeof(PackedVectorSampleEvent)))!;
    await Assert.That(read.Embedding.Select(BitConverter.SingleToUInt32Bits)).IsEquivalentTo(expected.Embedding.Select(BitConverter.SingleToUInt32Bits));
    await Assert.That(read.Lead.Embedding.Select(BitConverter.SingleToUInt32Bits)).IsEquivalentTo(expected.Lead.Embedding.Select(BitConverter.SingleToUInt32Bits));
    await Assert.That(read.Centroid).IsEquivalentTo(expected.Centroid);
    await Assert.That(read.Text).IsEqualTo(expected.Text);
  }

  private async Task<Guid> _storeAsync<TEvent>(NpgsqlConnection conn, string path, TEvent evt) where TEvent : IEvent {
    var payload = JsonSerializer.Serialize(evt, _json.GetTypeInfo(typeof(TEvent)));
    var id = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    switch (path) {
      case OUTBOX:
        await _emitOutboxAsync(conn, id, stream, TypeNameFormatter.AssemblyQualifiedName(typeof(TEvent)), payload);
        break;
      case INBOX:
        await _receiveAsync(id, stream, TypeNameFormatter.AssemblyQualifiedName(typeof(TEvent)), payload);
        break;
      default:
        await _executeAsync(conn, """
          SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, @body::jsonb, '{}'::jsonb, NULL, @at, 0)
          """, ("id", id), ("sid", stream), ("type", TypeNameFormatter.Format(typeof(TEvent))), ("body", payload), ("at", _t0));
        break;
    }
    return id;
  }

  private static async Task _emitOutboxAsync(NpgsqlConnection conn, Guid id, Guid stream, string type, string payload) {
    var instanceId = (Guid)TrackedGuid.New();
    await _executeAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
      VALUES
        (@id, 'vectors', @type, 'env', jsonb_build_object('id', @id::text, 'p', @payload::jsonb, 'h', '[]'::jsonb),
         '{}'::jsonb, jsonb_build_object('t', 'tenant-a'), 1, 0, @inst, @lease, 0, @sid, true, 0, @now)
      """,
      ("id", id), ("type", type), ("payload", payload), ("inst", instanceId), ("lease", _t0.AddMinutes(5)), ("sid", stream),
      ("now", _t0));
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, new[] { id });
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", _t0.AddMinutes(5));
    emit.Parameters.AddWithValue("now", _t0);
    _ = await emit.ExecuteScalarAsync();
  }

  private async Task _receiveAsync(Guid id, Guid stream, string type, string payload) {
    await using var dbContext = CreateDbContext();
    using var payloadDocument = JsonDocument.Parse(payload);
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(id),
      Payload = payloadDocument.RootElement.Clone(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var row = new InboxMessage {
      MessageId = id,
      HandlerName = "VectorHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = type,
      StreamId = stream,
      IsEvent = true,
      Flags = EventFlags.NoRebroadcast,
      Scope = new PerspectiveScope { TenantId = "tenant-a" },
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
    };
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, _json);
    await coordinator.StoreInboxMessagesAsync([row], partitionCount: 100);
    await using var conn = await _openAsync();
    var instanceId = (Guid)TrackedGuid.New();
    await _executeAsync(conn, "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = @lease WHERE message_id = @id",
      ("inst", instanceId), ("lease", _t0.AddMinutes(5)), ("id", id));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, @lease, @now, 4)",
      ("inst", instanceId), ("lease", _t0.AddMinutes(5)), ("now", _t0));
  }

  private ServiceProvider _services() =>
    new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton<ILibraryVersionProvider>(new LibraryVersionProvider("2.0.0"))
      .AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider((Guid)TrackedGuid.New(), "vector-svc", "host", 1))
      .BuildServiceProvider();

  private static VectorPackingGateStep _step() => new(NullLogger<VectorPackingGateStep>.Instance);

  private async Task _openTheGateAsync() {
    await _registerThisBuildAsync();
    await Assert.That(await _step().OpenAsync(_services(), CancellationToken.None)).IsEqualTo(VectorPackingGateStep.OPENED);
  }

  /// <summary>Registers an instance of this build through the EF Core driver's heartbeat.</summary>
  private async Task _registerThisBuildAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, _json);
    await Assert.That(await coordinator.RecordHeartbeatAsync(new HeartbeatRequest((Guid)TrackedGuid.New(), "vector-svc", "this-build", 1)))
      .IsTrue();
  }

  /// <summary>Registers a live instance the way the release before #2625 does: stored form level 1.</summary>
  private static async Task<Guid> _registerAnEarlierReleaseAsync(NpgsqlConnection conn) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn, "SELECT record_heartbeat(@i, 'vector-svc', 'earlier-release', 2, '{\"stored_form_level\": 1}'::jsonb)", ("i", id));
    return id;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync();
    return result is DBNull or null ? default! : (T)result;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
