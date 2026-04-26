using BenchmarkDotNet.Attributes;
using Npgsql;

namespace Whizbang.Benchmarks.Postgres;

/// <summary>
/// Headline benchmark for the work-pump decomposition: prove the empty-call
/// short-circuit on <c>claim_work</c> hits the ≤ 1 ms target.
/// Baseline (legacy <c>process_work_batch</c>) was ~17 ms per empty call.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class ClaimWorkBenchmarks {
  private PostgresFixture _fixture = null!;
  private NpgsqlDataSource _dataSource = null!;
  private NpgsqlConnection _conn = null!;
  private Guid _instanceId;

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

  /// <summary>
  /// Empty-queues call cost: 4-EXISTS short-circuit returns immediately.
  /// Target: ≤ 1 ms mean. Baseline (legacy <c>process_work_batch</c>): ~17 ms.
  /// </summary>
  [Benchmark(Description = "claim_work — empty queues (idle stack scenario)")]
  public async Task<int> IdleClaimCallCostAsync() {
    await using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_work($1, $2, $3, $4, $5, $6, $7)";
    cmd.Parameters.Add(new NpgsqlParameter { Value = _instanceId });
    cmd.Parameters.Add(new NpgsqlParameter { Value = "benchmark" });    // p_service_name
    cmd.Parameters.Add(new NpgsqlParameter { Value = "benchmark-host" });// p_host_name
    cmd.Parameters.Add(new NpgsqlParameter { Value = 1 });              // p_process_id
    cmd.Parameters.Add(new NpgsqlParameter { Value = 100 });            // p_max_streams
    cmd.Parameters.Add(new NpgsqlParameter { Value = 10000 });          // p_partition_count
    cmd.Parameters.Add(new NpgsqlParameter { Value = 300 });            // p_lease_seconds
    var rows = 0;
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows++;
    }
    return rows;
  }
}
