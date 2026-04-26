using BenchmarkDotNet.Attributes;
using Npgsql;

namespace Whizbang.Benchmarks.Postgres;

/// <summary>
/// Compares the SAVEPOINT-batched commit path (<c>commit_handler_batch</c>) against
/// the per-handler call path (<c>commit_handler_result</c>) under identical workload.
/// Proves the throughput multiplier: batched path commits N handlers with one fsync,
/// per-handler isolation via PL/pgSQL implicit savepoints. Plan target ≥ 25× speedup.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class HandlerCommitBenchmarks {
  private PostgresFixture _fixture = null!;
  private NpgsqlDataSource _dataSource = null!;
  private NpgsqlConnection _conn = null!;
  private Guid _instanceId;
  private Guid[] _messageIds = null!;

  [Params(10, 100)]
  public int BatchSize;

  [GlobalSetup]
  public void Setup() {
    _fixture = new PostgresFixture();
    _fixture.InitializeAsync().GetAwaiter().GetResult();
    _dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
    _conn = _dataSource.OpenConnection();
    _instanceId = Guid.NewGuid();
  }

  [GlobalCleanup]
  public void Cleanup() {
    _conn?.Dispose();
    _dataSource?.Dispose();
    _fixture?.DisposeAsync().AsTask().GetAwaiter().GetResult();
  }

  [IterationSetup]
  public void IterationSetup() {
    _messageIds = new Guid[BatchSize];
    using var insert = _conn.CreateCommand();
    insert.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         instance_id, lease_expiry, stream_id, partition_number)
      VALUES (@msg, 'BenchHandler', 'BenchEvent', '{}', '{}', 1, 0, NOW(),
              @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
    var msgParam = insert.Parameters.Add(new NpgsqlParameter("msg", System.Data.DbType.Guid));
    var instParam = insert.Parameters.Add(new NpgsqlParameter("inst", System.Data.DbType.Guid) { Value = _instanceId });
    var streamParam = insert.Parameters.Add(new NpgsqlParameter("stream", System.Data.DbType.Guid));
    insert.Prepare();

    for (var i = 0; i < BatchSize; i++) {
      var id = Guid.CreateVersion7();
      _messageIds[i] = id;
      msgParam.Value = id;
      streamParam.Value = Guid.CreateVersion7();
      insert.ExecuteNonQuery();
    }
  }

  /// <summary>
  /// Baseline: call commit_handler_result N times. One fsync per call.
  /// </summary>
  [Benchmark(Baseline = true, Description = "commit_handler_result × N (one fsync per handler)")]
  public async Task SerialAsync() {
    for (var i = 0; i < BatchSize; i++) {
      var json = $$"""
        {"handler_id": "{{Guid.CreateVersion7()}}", "instance_id": "{{_instanceId}}",
         "inbox_completion": {"MessageId": "{{_messageIds[i]}}", "Status": 4}, "new_outbox_messages": []}
        """;
      await using var cmd = _conn.CreateCommand();
      cmd.CommandText = "SELECT commit_handler_result($1::jsonb)";
      cmd.Parameters.Add(new NpgsqlParameter { Value = json });
      await cmd.ExecuteNonQueryAsync();
    }
  }

  /// <summary>
  /// New path: commit_handler_batch with N items in one round-trip, single fsync.
  /// Plan target: ≥ 25× speedup vs serial.
  /// </summary>
  [Benchmark(Description = "commit_handler_batch (single fsync, SAVEPOINT-per-handler)")]
  public async Task<int> BatchedAsync() {
    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < BatchSize; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append("{\"handler_id\":\"").Append(Guid.CreateVersion7()).Append("\",")
        .Append("\"instance_id\":\"").Append(_instanceId).Append("\",")
        .Append("\"inbox_completion\":{\"MessageId\":\"").Append(_messageIds[i]).Append("\",\"Status\":4},")
        .Append("\"new_outbox_messages\":[]}");
    }
    sb.Append(']');

    await using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM commit_handler_batch($1::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter { Value = sb.ToString() });
    var rows = 0;
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows++;
    }
    return rows;
  }
}
