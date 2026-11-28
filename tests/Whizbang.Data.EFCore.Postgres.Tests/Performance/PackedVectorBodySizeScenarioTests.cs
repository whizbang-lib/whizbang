// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
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

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>An embedding event of the size the vector storage plan measured: one 3,072-dimension vector.</summary>
/// <param name="ChunkId">The stream.</param>
/// <param name="Text">The text the vector was made from.</param>
/// <param name="Embedding">Its embedding.</param>
public sealed record PackedVectorMeasuredEvent([property: StreamId] Guid ChunkId, string Text, float[] Embedding) : IEvent;

/// <summary>
/// #2628 (V5 of the vector storage plan): what packing saves on a stored vector event body. The same events are stored
/// as JSON numbers with the gate closed, measured, packed by the history pass (#2627), measured again, and the packed
/// vector's raw bits are measured beside them, which is what a <c>bytea</c> form would hold.
/// </summary>
/// <remarks>
/// <para>
/// Two corpora of <see cref="EVENTS"/> events, each one 3,072-dimension vector of values drawn like a normalized
/// embedding (about +/-0.05) and a short text: at full precision (each value its float's shortest round-trip text, as a
/// model's output is), and rounded to 4 decimals (short numbers, which compress well).
/// </para>
/// <para>
/// Reported per event: the body's text length (what the JSON is), its stored size (<c>pg_column_size</c>, after the
/// column's lz4 compression, which is what the table pays), and the stored size of the raw bits alone. The table's size
/// after a rewrite (<c>VACUUM FULL</c>) before and after. Gates: the packed text is always shorter; at full precision the
/// stored body is over 30% smaller; with 4 decimals it is larger, which the docs record as the trade-off.
/// </para>
/// <para>
/// Measured (PostgreSQL in the local test container, 2026-11-28, per event): full precision, JSON numbers 41,218
/// characters stored in 26,613 bytes after lz4, packed 16,472 characters stored in 16,476 bytes (38.1% smaller; the table
/// after a rewrite 35.4% smaller); 4 decimals, JSON numbers 25,850 characters stored in 11,351 bytes, packed stored in
/// 12,960 bytes (14.2% larger). The raw bits alone store in 12,292 bytes. jsonb holds each number as a numeric, which lz4
/// compresses in proportion to how few digits it has, while binary32 bits of a decimal value are close to random: the
/// saving depends on the vectors' digits, not on their dimension.
/// </para>
/// </remarks>
/// <docs>data/event-store#what-packing-saves</docs>
[Category("Benchmark")]
[NotInParallel] // A measurement: runs alone so it measures the code, not its neighbors.
[Category("Performance")]
public class PackedVectorBodySizeScenarioTests : EFCoreTestBase {
  private const int EVENTS = 200;
  private const int DIMENSIONS = 3072;
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private static readonly DateTimeOffset _t0 = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);

  [Test]
  [Timeout(1800000)]
  [Arguments(0)]
  [Arguments(4)]
  public async Task StoredBody_BeforeAndAfterPacking_ReportAsync(int decimals, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var type = TypeNameFormatter.Format(typeof(PackedVectorMeasuredEvent));
    var earlier = (Guid)TrackedGuid.New();
    await _executeAsync(conn, "SELECT record_heartbeat(@i, 'vector-svc', 'earlier-release', 2, '{\"stored_form_level\": 1}'::jsonb)", ("i", earlier));
    var random = new Random(2628);
    for (var i = 0; i < EVENTS; i++) {
      var vector = new float[DIMENSIONS];
      for (var d = 0; d < DIMENSIONS; d++) {
        var value = (random.NextDouble() - 0.5) / 10;
        vector[d] = (float)(decimals == 0 ? value : Math.Round(value, decimals));
      }
      var payload = JsonSerializer.Serialize(new PackedVectorMeasuredEvent((Guid)TrackedGuid.New(), $"chunk {i}", vector),
        _json.GetTypeInfo(typeof(PackedVectorMeasuredEvent)));
      var stream = (Guid)TrackedGuid.New();
      await _executeAsync(conn, """
        SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, @body::jsonb, '{}'::jsonb, NULL, @at, 0)
        """, ("id", (Guid)TrackedGuid.New()), ("sid", stream), ("type", type), ("body", payload), ("at", _t0));
    }
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(100000)");

    var before = await _measureAsync(conn, type, cancellationToken);
    await _executeAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @i", ("i", earlier));
    var services = _services();
    await using (var dbContext = CreateDbContext()) {
      await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, _json)
        .RecordHeartbeatAsync(new HeartbeatRequest((Guid)TrackedGuid.New(), "vector-svc", "this-build", 1), cancellationToken);
    }
    await Assert.That(await new VectorPackingGateStep(NullLogger<VectorPackingGateStep>.Instance).OpenAsync(services, cancellationToken))
      .IsEqualTo(VectorPackingGateStep.OPENED);
    StoredVectorConversionResult pass;
    do {
      pass = await StoredVectorConversionStep.FromServices(services).ConvertAsync(services, cancellationToken);
    } while (pass.Status == "ok");
    var after = await _measureAsync(conn, type, cancellationToken);
    var raw = await _scalarAsync<double>(conn, """
      SELECT avg(pg_column_size(decode(b.event_data ->> 'Embedding', 'base64')))::float8
      FROM wh_event_body b JOIN wh_event_store es ON es.event_id = b.event_id WHERE es.event_type = @t
      """, ("t", type));

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
      $"Vector event body, {EVENTS} events of {DIMENSIONS} floats ({(decimals == 0 ? "full precision" : $"{decimals} decimals")}): JSON numbers {before.Text:F0} chars, stored {before.Stored:F0} B; " +
      $"packed {after.Text:F0} chars, stored {after.Stored:F0} B ({1 - (after.Stored / before.Stored):P1} smaller stored, " +
      $"{1 - (after.Text / before.Text):P1} smaller as text); raw bits stored {raw:F0} B; " +
      $"table after a rewrite {before.Table:N0} B -> {after.Table:N0} B ({1 - ((double)after.Table / before.Table):P1} smaller)"));

    await Assert.That(after.Text).IsLessThan(before.Text).Because("base64 of the bits is shorter than the JSON numbers' text");
    await Assert.That(raw).IsLessThan(after.Stored + 1).Because("the raw bits are the floor any binary32 form can reach");
    if (decimals == 0) {
      // Full-precision floats, as a model writes them: the numbers' digits do not compress, so packing wins.
      await Assert.That(after.Stored).IsLessThan(before.Stored * 0.7).Because("packing saves over 30% of a full-precision vector body");
      await Assert.That(after.Table).IsLessThan(before.Table);
    } else {
      // Short decimals compress well as numeric; their binary32 bits do not, so packing costs space. Locked so the
      // documented trade-off cannot change unnoticed.
      await Assert.That(after.Stored).IsGreaterThan(before.Stored).Because("lz4 compresses short decimal numerics below the packed bits");
    }
  }

  private readonly record struct Measure(double Text, double Stored, long Table);

  private static async Task<Measure> _measureAsync(NpgsqlConnection conn, string type, CancellationToken cancellationToken) {
    await _executeAsync(conn, "VACUUM FULL wh_event_body");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT avg(octet_length(b.event_data::text))::float8, avg(pg_column_size(b.event_data))::float8,
             pg_total_relation_size('wh_event_body')
      FROM wh_event_body b JOIN wh_event_store es ON es.event_id = b.event_id WHERE es.event_type = @t
      """;
    cmd.Parameters.AddWithValue("t", type);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    await reader.ReadAsync(cancellationToken);
    return new Measure(reader.GetDouble(0), reader.GetDouble(1), reader.GetInt64(2));
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

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
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
