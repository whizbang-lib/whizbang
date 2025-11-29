using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks for multiple SerialExecutor streams running in parallel.
/// Simulates real-world aggregate processing where each aggregate has serial ordering
/// but different aggregates can be processed concurrently.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class MultiStreamExecutorBenchmarks {
  private record TestCommand(string AggregateId, int SequenceNumber);

  private IExecutionStrategy[] _executors = null!;
  private IMessageEnvelope[] _envelopes = null!;
  private PolicyContext[] _contexts = null!;
  private const int StreamCount = 10;
  private const int MessagesPerStream = 100;

  [GlobalSetup]
  public async Task Setup() {
    _executors = new IExecutionStrategy[StreamCount];
    _envelopes = new IMessageEnvelope[StreamCount];
    _contexts = new PolicyContext[StreamCount];

    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();

    // Create independent SerialExecutor instances for each stream
    for (int i = 0; i < StreamCount; i++) {
      _executors[i] = new SerialExecutor();
      await _executors[i].StartAsync();

      var message = new TestCommand($"aggregate-{i}", 0);
      var envelope = new MessageEnvelope<TestCommand> {
        MessageId = MessageId.New(),
        Payload = message,
        Hops = []
      };
      envelope.AddHop(new MessageHop {
        Type = HopType.Current,
        ServiceName = "Benchmark",
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New()
      });

      _envelopes[i] = envelope;
      _contexts[i] = new PolicyContext(
        message: message,
        envelope: envelope,
        services: serviceProvider,
        environment: "benchmark"
      );
    }
  }

  [GlobalCleanup]
  public async Task Cleanup() {
    foreach (var executor in _executors) {
      await executor.StopAsync();
    }
  }

  /// <summary>
  /// Baseline: Single SerialExecutor processing messages serially.
  /// </summary>
  [Benchmark(Baseline = true)]
  public async Task SingleStream_100Messages() {
    for (int i = 0; i < MessagesPerStream; i++) {
      await _executors[0].ExecuteAsync<int>(
        _envelopes[0],
        (env, ctx) => ValueTask.FromResult(i),
        _contexts[0]
      );
    }
  }

  /// <summary>
  /// Multiple SerialExecutor streams processing in parallel.
  /// Each stream maintains serial ordering, but streams run concurrently.
  /// Total: 10 streams × 100 messages = 1000 messages processed.
  /// </summary>
  [Benchmark]
  public async Task MultiStream_10x100Messages() {
    var tasks = new Task[StreamCount];

    for (int streamId = 0; streamId < StreamCount; streamId++) {
      var id = streamId; // Capture for closure
      tasks[id] = Task.Run(async () => {
        for (int i = 0; i < MessagesPerStream; i++) {
          await _executors[id].ExecuteAsync<int>(
            _envelopes[id],
            (env, ctx) => ValueTask.FromResult(i),
            _contexts[id]
          );
        }
      });
    }

    await Task.WhenAll(tasks);
  }

  /// <summary>
  /// Comparison: Single ParallelExecutor processing 1000 messages.
  /// Shows the performance difference between multi-stream serial vs single parallel.
  /// </summary>
  [Benchmark]
  public async Task ParallelExecutor_1000Messages() {
    var parallelExecutor = new ParallelExecutor(maxConcurrency: 10);
    await parallelExecutor.StartAsync();

    try {
      var tasks = new Task<int>[StreamCount * MessagesPerStream];
      for (int i = 0; i < tasks.Length; i++) {
        var capturedI = i;
        tasks[i] = parallelExecutor.ExecuteAsync<int>(
          _envelopes[0],
          (env, ctx) => ValueTask.FromResult(capturedI),
          _contexts[0]
        ).AsTask();
      }

      await Task.WhenAll(tasks);
    } finally {
      await parallelExecutor.StopAsync();
    }
  }
}
