using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Workers;

namespace Whizbang.Benchmarks;

/// <summary>
/// Measures the BatchFlusher's coalescing efficiency under various producer rates.
/// Compares "1 round-trip per item" baseline vs the Nagle pattern that target
/// "≤ 5 round-trips per 1000 items in a 100ms window" (per work-pump plan).
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class BatchFlusherBenchmarks {
  [Params(100, 1_000, 10_000)]
  public int ItemCount;

  [Benchmark(Baseline = true, Description = "Direct flush per item — no coalescing")]
  public async Task<int> DirectFlush_PerItemAsync() {
    var calls = 0;
    Task FlushAsync(IReadOnlyList<int> _, CancellationToken __) {
      calls++;
      return Task.CompletedTask;
    }
    await using var flusher = new BatchFlusher<int>(
      flush: FlushAsync,
      options: new BatchFlusherOptions {
        MaxBatchSize = 1,
        CoalesceWindowMs = 0,
        ImmediateFlushThreshold = 1,
        ChannelCapacity = ItemCount
      },
      logger: NullLogger.Instance);

    for (var i = 0; i < ItemCount; i++) {
      await flusher.Writer.WriteAsync(i);
    }
    flusher.Writer.Complete();
    await flusher.StoppedSignal;
    return calls;
  }

  [Benchmark(Description = "Nagle-coalesced flush — default options")]
  public async Task<int> CoalescedFlush_DefaultsAsync() {
    var calls = 0;
    Task FlushAsync(IReadOnlyList<int> _, CancellationToken __) {
      calls++;
      return Task.CompletedTask;
    }
    await using var flusher = new BatchFlusher<int>(
      flush: FlushAsync,
      options: new BatchFlusherOptions {
        MaxBatchSize = 500,
        CoalesceWindowMs = 25,
        ImmediateFlushThreshold = 250,
        ChannelCapacity = ItemCount
      },
      logger: NullLogger.Instance);

    for (var i = 0; i < ItemCount; i++) {
      await flusher.Writer.WriteAsync(i);
    }
    flusher.Writer.Complete();
    await flusher.StoppedSignal;
    return calls;
  }

  [Benchmark(Description = "Coalesced — outbox-completion tuning (10ms window, 250 threshold)")]
  public async Task<int> CoalescedFlush_OutboxTuningAsync() {
    var calls = 0;
    Task FlushAsync(IReadOnlyList<int> _, CancellationToken __) {
      calls++;
      return Task.CompletedTask;
    }
    await using var flusher = new BatchFlusher<int>(
      flush: FlushAsync,
      options: new BatchFlusherOptions {
        MaxBatchSize = 500,
        CoalesceWindowMs = 10,
        ImmediateFlushThreshold = 250,
        ChannelCapacity = ItemCount
      },
      logger: NullLogger.Instance);

    for (var i = 0; i < ItemCount; i++) {
      await flusher.Writer.WriteAsync(i);
    }
    flusher.Writer.Complete();
    await flusher.StoppedSignal;
    return calls;
  }
}
