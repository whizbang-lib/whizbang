using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks for execution strategies (SerialExecutor vs ParallelExecutor).
/// Measures throughput and overhead of different execution models.
/// </summary>
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[MarkdownExporter]
public class ExecutorBenchmarks {
  private record TestCommand(string Id, int Value);

  private IExecutionStrategy _serialExecutor = null!;
  private IExecutionStrategy _parallelExecutor = null!;
  private IMessageEnvelope _envelope = null!;
  private PolicyContext _context = null!;

  [GlobalSetup]
  public async Task Setup() {
    _serialExecutor = new SerialExecutor();
    _parallelExecutor = new ParallelExecutor(maxConcurrency: 10);

    // Start executors
    await _serialExecutor.StartAsync();
    await _parallelExecutor.StartAsync();

    var message = new TestCommand("test-123", 42);
    _envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
      Payload = message,
      Hops = new List<MessageHop>()
    };
    _envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "Benchmark",
      Timestamp = DateTimeOffset.UtcNow
    });

    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    _context = new PolicyContext(
      message: message,
      envelope: _envelope,
      services: serviceProvider,
      environment: "benchmark"
    );
  }

  [GlobalCleanup]
  public async Task Cleanup() {
    await _serialExecutor.StopAsync();
    await _parallelExecutor.StopAsync();
  }

  [Benchmark(Baseline = true)]
  public async Task<int> SerialExecutor_SingleMessage() {
    return await _serialExecutor.ExecuteAsync<int>(
      _envelope,
      (env, ctx) => Task.FromResult(((TestCommand)ctx.Message).Value),
      _context
    );
  }

  [Benchmark]
  public async Task<int> ParallelExecutor_SingleMessage() {
    return await _parallelExecutor.ExecuteAsync<int>(
      _envelope,
      (env, ctx) => Task.FromResult(((TestCommand)ctx.Message).Value),
      _context
    );
  }

  [Benchmark]
  public async Task SerialExecutor_100Messages() {
    var tasks = new List<Task<int>>();
    for (int i = 0; i < 100; i++) {
      tasks.Add(_serialExecutor.ExecuteAsync<int>(
        _envelope,
        (env, ctx) => Task.FromResult(i),
        _context
      ));
    }
    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public async Task ParallelExecutor_100Messages() {
    var tasks = new List<Task<int>>();
    for (int i = 0; i < 100; i++) {
      tasks.Add(_parallelExecutor.ExecuteAsync<int>(
        _envelope,
        (env, ctx) => Task.FromResult(i),
        _context
      ));
    }
    await Task.WhenAll(tasks);
  }
}
