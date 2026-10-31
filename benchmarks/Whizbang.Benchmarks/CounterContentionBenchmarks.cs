// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace Whizbang.Benchmarks;

/// <summary>
/// Whether a shared <see cref="Interlocked.Increment(ref long)"/> counter written from many threads per message is
/// worth replacing with a per-core striped counter (#1756).
/// </summary>
/// <remarks>
/// <para>
/// Every thread runs 200,000 iterations; each does <see cref="WorkPerIncrement"/> units of
/// unrelated arithmetic (one xorshift step, about a nanosecond) and then one increment. Zero work is the pathological
/// case: threads do nothing but fight over one cache line. 1,000 units is still far less than the managed cost of
/// handling one message, before any I/O. Each operation is one whole run, thread start included (the
/// <see cref="NoCounter"/> row is that floor); divide by 200,000 for the cost of one iteration on one thread.
/// </para>
/// <para>
/// The striped counter here is written for the measurement and is not shipped unless the numbers justify it: one
/// slot per processor, each padded to 128 bytes (the cache line of the measuring machine, an Apple M-series),
/// indexed by <see cref="Thread.GetCurrentProcessorId"/>.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[MarkdownExporter]
public class CounterContentionBenchmarks {
  private const int INCREMENTS_PER_THREAD = 200_000;

  [Params(1, 4, 10)]
  public int Threads { get; set; }

  [Params(0, 100, 1000)]
  public int WorkPerIncrement { get; set; }

  private long _shared;
  private StripedCounter _striped = null!;

  [GlobalSetup]
  public void Setup() => _striped = new StripedCounter();

  [Benchmark(Baseline = true)]
  public long SharedInterlocked() {
    _run(Threads, WorkPerIncrement, () => Interlocked.Increment(ref _shared));
    return Interlocked.Read(ref _shared);
  }

  [Benchmark]
  public long Striped() {
    _run(Threads, WorkPerIncrement, _striped.Increment);
    return _striped.Sum();
  }

  [Benchmark]
  public long NoCounter() {
    _run(Threads, WorkPerIncrement, static () => { });
    return 0;
  }

  private static void _run(int threads, int work, Action increment) {
    var workers = new Thread[threads];
    using var start = new ManualResetEventSlim(false);
    for (var t = 0; t < threads; t++) {
      workers[t] = new Thread(() => {
        start.Wait();
        var seed = (uint)Environment.CurrentManagedThreadId | 1u;
        for (var i = 0; i < INCREMENTS_PER_THREAD; i++) {
          for (var w = 0; w < work; w++) {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
          }
          if (seed == 0) {
            // Never true (xorshift of a non-zero seed stays non-zero); keeps the work from being optimized away.
            throw new InvalidOperationException();
          }
          increment();
        }
      });
      workers[t].Start();
    }
    start.Set();
    foreach (var worker in workers) {
      worker.Join();
    }
  }

  /// <summary>One padded slot per processor; a writer touches only the slot of the processor it runs on.</summary>
  private sealed class StripedCounter {
    private readonly Slot[] _slots = new Slot[_roundUpToPowerOfTwo(Environment.ProcessorCount)];

    public void Increment() => Interlocked.Increment(ref _slots[Thread.GetCurrentProcessorId() & (_slots.Length - 1)].Value);

    public long Sum() {
      long sum = 0;
      for (var i = 0; i < _slots.Length; i++) {
        sum += Volatile.Read(ref _slots[i].Value);
      }
      return sum;
    }

    private static int _roundUpToPowerOfTwo(int value) {
      var result = 1;
      while (result < value) {
        result <<= 1;
      }
      return result;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Slot {
      [FieldOffset(0)] public long Value;
    }
  }
}
