using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Whizbang.Core.Pooling;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks for PooledValueTaskSource{T} and PooledSourcePool{T}.
/// Measures allocation overhead of pooled vs non-pooled async completions.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public static class PoolingBenchmarks {
  // ============================================================================
  // SINGLE OPERATION BENCHMARKS
  // ============================================================================

  [Benchmark(Baseline = true)]
  public static async Task<int> NonPooled_SingleOperation() {
    // Traditional approach: new source every time (allocates)
    var source = new PooledValueTaskSource<int>();
    source.SetResult(42);
    var valueTask = new ValueTask<int>(source, source.Token);
    return await valueTask;
  }

  [Benchmark]
  public static async Task<int> Pooled_SingleOperation() {
    // Pooled approach: rent and return
    var source = PooledSourcePool<int>.Rent();
    source.Reset();
    source.SetResult(42);
    var valueTask = new ValueTask<int>(source, source.Token);
    var result = await valueTask;
    source.Reset();
    PooledSourcePool<int>.Return(source);
    return result;
  }

  // ============================================================================
  // HIGH THROUGHPUT BENCHMARKS
  // ============================================================================

  [Benchmark]
  public static async Task NonPooled_100Operations() {
    for (int i = 0; i < 100; i++) {
      var source = new PooledValueTaskSource<int>();
      source.SetResult(i);
      var valueTask = new ValueTask<int>(source, source.Token);
      await valueTask;
    }
  }

  [Benchmark]
  public static async Task Pooled_100Operations() {
    for (int i = 0; i < 100; i++) {
      var source = PooledSourcePool<int>.Rent();
      source.Reset();
      source.SetResult(i);
      var valueTask = new ValueTask<int>(source, source.Token);
      await valueTask;
      source.Reset();
      PooledSourcePool<int>.Return(source);
    }
  }

  [Benchmark]
  public static async Task NonPooled_1000Operations() {
    for (int i = 0; i < 1000; i++) {
      var source = new PooledValueTaskSource<int>();
      source.SetResult(i);
      var valueTask = new ValueTask<int>(source, source.Token);
      await valueTask;
    }
  }

  [Benchmark]
  public static async Task Pooled_1000Operations() {
    for (int i = 0; i < 1000; i++) {
      var source = PooledSourcePool<int>.Rent();
      source.Reset();
      source.SetResult(i);
      var valueTask = new ValueTask<int>(source, source.Token);
      await valueTask;
      source.Reset();
      PooledSourcePool<int>.Return(source);
    }
  }

  // ============================================================================
  // TYPE SIZE COMPARISON BENCHMARKS
  // ============================================================================

  [Benchmark]
  public static async Task<int> Pooled_IntType() {
    var source = PooledSourcePool<int>.Rent();
    source.Reset();
    source.SetResult(42);
    var valueTask = new ValueTask<int>(source, source.Token);
    var result = await valueTask;
    source.Reset();
    PooledSourcePool<int>.Return(source);
    return result;
  }

  [Benchmark]
  public static async Task<string> Pooled_StringType() {
    var source = PooledSourcePool<string>.Rent();
    source.Reset();
    source.SetResult("hello");
    var valueTask = new ValueTask<string>(source, source.Token);
    var result = await valueTask;
    source.Reset();
    PooledSourcePool<string>.Return(source);
    return result;
  }

  [Benchmark]
  public static async Task<LargeStruct> Pooled_LargeStructType() {
    var source = PooledSourcePool<LargeStruct>.Rent();
    source.Reset();
    source.SetResult(new LargeStruct { Value = 42 });
    var valueTask = new ValueTask<LargeStruct>(source, source.Token);
    var result = await valueTask;
    source.Reset();
    PooledSourcePool<LargeStruct>.Return(source);
    return result;
  }

  // ============================================================================
  // RENT/RETURN OVERHEAD BENCHMARKS
  // ============================================================================

  [Benchmark]
  public static void RentReturn_EmptyPool() {
    // Worst case: pool is empty, must allocate
    var source = PooledSourcePool<int>.Rent();
    source.Reset();
    PooledSourcePool<int>.Return(source);
  }

  [Benchmark]
  public static void RentReturn_WarmPool() {
    // Best case: pool already has instances
    // Pre-warm the pool
    var sources = new List<PooledValueTaskSource<int>>();
    for (int i = 0; i < 10; i++) {
      sources.Add(PooledSourcePool<int>.Rent());
    }
    foreach (var s in sources) {
      s.Reset();
      PooledSourcePool<int>.Return(s);
    }

    // Now benchmark with warm pool
    var source = PooledSourcePool<int>.Rent();
    source.Reset();
    PooledSourcePool<int>.Return(source);
  }

  // ============================================================================
  // CONCURRENT ACCESS BENCHMARKS
  // ============================================================================

  [Benchmark]
  public static async Task Pooled_ConcurrentAccess_10Threads() {
    var tasks = new List<Task>();
    for (int t = 0; t < 10; t++) {
      tasks.Add(Task.Run(async () => {
        for (int i = 0; i < 10; i++) {
          var source = PooledSourcePool<int>.Rent();
          source.Reset();
          source.SetResult(i);
          var valueTask = new ValueTask<int>(source, source.Token);
          await valueTask;
          source.Reset();
          PooledSourcePool<int>.Return(source);
        }
      }));
    }
    await Task.WhenAll(tasks);
  }

  // ============================================================================
  // REALISTIC SCENARIO BENCHMARKS
  // ============================================================================

  [Benchmark]
  public static async Task RealisticScenario_MessageProcessing_100Messages() {
    // Simulates typical message processing with pooling
    for (int i = 0; i < 100; i++) {
      var source = PooledSourcePool<ProcessingResult>.Rent();
      source.Reset();

      // Simulate async processing
      await Task.Yield();

      source.SetResult(new ProcessingResult {
        MessageId = i,
        Status = "Success",
        Duration = TimeSpan.FromMilliseconds(10)
      });

      var valueTask = new ValueTask<ProcessingResult>(source, source.Token);
      var result = await valueTask;

      source.Reset();
      PooledSourcePool<ProcessingResult>.Return(source);
    }
  }

  // ============================================================================
  // HELPER TYPES
  // ============================================================================

  public struct LargeStruct {
    public int Value;
    public long Timestamp;
    public string Data;
    public Guid Id;
  }

  public record ProcessingResult {
    public int MessageId { get; init; }
    public string Status { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
  }
}
