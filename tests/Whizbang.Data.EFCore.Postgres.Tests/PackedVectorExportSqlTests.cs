// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2626 (V3 of the vector storage plan): a stored body that leaves the database carries its vectors as JSON numbers.
/// The redelivery selection, which an integrity repair sends to a peer and the audit rebuild re-records, and the trace
/// store, which hands the payload to consumer code, read the body through <c>wh_event_body_wire_form</c>, so a reader
/// with no packed-form converter (a plain JSON reader, another service, an older build) reads the numbers.
/// </summary>
/// <docs>data/event-store#what-leaves-the-database</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/721_StoredBodiesLeaveInTheWireForm.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class PackedVectorExportSqlTests : EFCoreTestBase {
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private static readonly DateTimeOffset _t0 = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task Redelivery_APackedEvent_IsSelectedInTheWireForm_WhichAPlainReaderReadsAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    var sample = _sample();
    var (id, stream) = await _appendAsync(conn, sample);
    await Assert.That(await _scalarAsync<string>(conn, "SELECT jsonb_typeof(event_data -> 'Embedding') FROM wh_event_body WHERE event_id = @e",
      ("e", id))).IsEqualTo("string").Because("control: the stored body is packed");

    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, _json);
    var selected = await coordinator.SelectRedeliveryEventsAsync(new RedeliveryRequest { StreamIds = [stream] });

    var copy = JsonNode.Parse(selected.Single().EventData)!;
    await _assertPlainReaderReadsAsync(copy, sample);
  }

  [Test]
  public async Task TraceStore_APackedEvent_IsReadInTheWireFormAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    var sample = _sample();
    var (id, _) = await _appendAsync(conn, sample);

    var payload = JsonNode.Parse(await _scalarAsync<string>(conn, "SELECT event_data::text FROM wh_trace_metadata(@ids)",
      ("ids", new[] { id })))!;

    await _assertPlainReaderReadsAsync(payload, sample);
  }

  [Test]
  public async Task WireForm_WritesBackTheNumbersAPackedVectorHeld_BitExactAsync() {
    await using var conn = await _openAsync();
    var random = new Random(2626);
    float[] values = [0.1f, -2.5f, float.MaxValue, -float.MaxValue, float.Epsilon, -float.Epsilon, 1.17549435E-38f, 0f,
      .. Enumerable.Range(0, 3072).Select(_ => (float)((random.NextDouble() * 2) - 1))];
    var arrayText = JsonSerializer.Serialize(values, _json.GetTypeInfo(typeof(float[])));

    var roundTrip = await _scalarAsync<string>(conn,
      "SELECT _wh_unpack_vector(_wh_pack_vector(@a::jsonb))::text", ("a", arrayText));

    var read = JsonNode.Parse(roundTrip)!.AsArray().Select(static n => n!.GetValue<float>()).ToArray();
    await Assert.That(read.Select(BitConverter.SingleToUInt32Bits)).IsEquivalentTo(values.Select(BitConverter.SingleToUInt32Bits),
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  [Arguments("\"not base64!\"")]
  [Arguments("\"AAA=\"")]
  [Arguments("\"\"")]
  [Arguments("\"f4AAAA==\"")]
  [Arguments("[1, 2]")]
  [Arguments("7")]
  public async Task WireForm_LeavesAValueThatIsNotAPackedVector_AsItIsAsync(string value) {
    await using var conn = await _openAsync();

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT _wh_unpack_vector(@v::jsonb) = @v::jsonb", ("v", value))).IsTrue()
      .Because($"{value} is not base64 of whole finite binary32 values");
  }

  [Test]
  public async Task WireForm_ATypeWithNoPaths_OrANullBody_IsReturnedAsItIsAsync() {
    await using var conn = await _openAsync();

    await Assert.That(await _scalarAsync<string>(conn,
      "SELECT wh_event_body_wire_form('Some.Unregistered, Elsewhere', '{\"Embedding\": \"P4AAAA==\"}'::jsonb)::text"))
      .IsEqualTo("""{"Embedding": "P4AAAA=="}""");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_event_body_wire_form('x', NULL) IS NULL")).IsTrue();
  }

  // ===== Helpers =====

  private static async Task _assertPlainReaderReadsAsync(JsonNode payload, PackedVectorSampleEvent expected) {
    // A reader with no packed-form converter: the numbers, read one by one.
    var embedding = payload["Embedding"]!.AsArray().Select(static n => n!.GetValue<float>());
    var lead = payload["Lead"]!["Embedding"]!.AsArray().Select(static n => n!.GetValue<float>());
    await Assert.That(embedding.Select(BitConverter.SingleToUInt32Bits))
      .IsEquivalentTo(expected.Embedding.Select(BitConverter.SingleToUInt32Bits), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(lead.Select(BitConverter.SingleToUInt32Bits))
      .IsEquivalentTo(expected.Lead.Embedding.Select(BitConverter.SingleToUInt32Bits), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(payload["Text"]!.GetValue<string>()).IsEqualTo(expected.Text);
  }

  private static PackedVectorSampleEvent _sample() {
    var random = new Random(7);
    return new PackedVectorSampleEvent {
      PassageId = (Guid)TrackedGuid.New(),
      Text = "exported",
      Embedding = [.. Enumerable.Range(0, 32).Select(_ => (float)random.NextDouble()), float.Epsilon, float.MaxValue],
      Lead = new PackedVectorChunk("lead", [0.25f, -1f / 3f]),
      Centroid = [1f],
    };
  }

  private static async Task<(Guid Id, Guid Stream)> _appendAsync(NpgsqlConnection conn, PackedVectorSampleEvent sample) {
    var id = (Guid)TrackedGuid.New();
    var stream = sample.PassageId;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, @body::jsonb, '{}'::jsonb, NULL, @at, 0)
      """;
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("sid", stream);
    cmd.Parameters.AddWithValue("type", TypeNameFormatter.Format(typeof(PackedVectorSampleEvent)));
    cmd.Parameters.AddWithValue("body", JsonSerializer.Serialize(sample, _json.GetTypeInfo(typeof(PackedVectorSampleEvent))));
    cmd.Parameters.AddWithValue("at", _t0);
    _ = await cmd.ExecuteScalarAsync();
    return (id, stream);
  }

  private async Task _openTheGateAsync() {
    await using (var dbContext = CreateDbContext()) {
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, _json);
      await Assert.That(await coordinator.RecordHeartbeatAsync(new HeartbeatRequest((Guid)TrackedGuid.New(), "vector-svc", "this-build", 1)))
        .IsTrue();
    }
    await using var services = new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton<ILibraryVersionProvider>(new LibraryVersionProvider("2.0.0"))
      .AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider((Guid)TrackedGuid.New(), "vector-svc", "host", 1))
      .BuildServiceProvider();
    await Assert.That(await new VectorPackingGateStep(NullLogger<VectorPackingGateStep>.Instance).OpenAsync(services, CancellationToken.None))
      .IsEqualTo(VectorPackingGateStep.OPENED);
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
}
