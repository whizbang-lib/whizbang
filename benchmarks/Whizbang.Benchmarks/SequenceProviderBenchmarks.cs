using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Whizbang.Core.Sequencing;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks for sequence providers.
/// Measures throughput of sequence number generation, both single-threaded and concurrent.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class SequenceProviderBenchmarks {
  private ISequenceProvider _provider = null!;
  private const string StreamKey = "benchmark-stream";

  [GlobalSetup]
  public void Setup() {
    _provider = new InMemorySequenceProvider();
  }

  [Benchmark(Baseline = true)]
  public async Task<long> SingleSequence() {
    return await _provider.GetNextAsync(StreamKey);
  }

  [Benchmark]
  public async Task Generate100Sequences_Serial() {
    for (int i = 0; i < 100; i++) {
      await _provider.GetNextAsync(StreamKey);
    }
  }

  [Benchmark]
  public async Task Generate100Sequences_Parallel() {
    var tasks = new List<Task<long>>();
    for (int i = 0; i < 100; i++) {
      tasks.Add(_provider.GetNextAsync(StreamKey));
    }
    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public async Task Generate1000Sequences_Parallel() {
    var tasks = new List<Task<long>>();
    for (int i = 0; i < 1000; i++) {
      tasks.Add(_provider.GetNextAsync(StreamKey));
    }
    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public async Task MultipleStreams_10Streams_100Each() {
    var tasks = new List<Task<long>>();
    for (int stream = 0; stream < 10; stream++) {
      var streamKey = $"stream-{stream}";
      for (int i = 0; i < 100; i++) {
        tasks.Add(_provider.GetNextAsync(streamKey));
      }
    }
    await Task.WhenAll(tasks);
  }
}
