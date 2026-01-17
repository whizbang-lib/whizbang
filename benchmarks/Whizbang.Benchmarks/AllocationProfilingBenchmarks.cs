using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Pooling;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Detailed allocation profiling to understand sources of allocations.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class AllocationProfilingBenchmarks {
  private const string BENCHMARK_HOST = "benchmark-host";
  private const int BENCHMARK_PROCESS_ID = 12345;
  private const string BENCHMARK_ENVIRONMENT = "benchmark";

  private sealed record TestCommand(string Id, int Value);

  private ParallelExecutor _parallelExecutor = null!;
  private SerialExecutor _serialExecutor = null!;
  private MessageEnvelope<TestCommand> _envelope = null!;
  private PolicyContext _context = null!;
  private PolicyContext _pooledContext = null!;

  // Pre-allocated handler to avoid lambda allocation
  private Func<IMessageEnvelope, PolicyContext, ValueTask<int>> _handler = null!;

  [GlobalSetup]
  public async Task SetupAsync() {
    _parallelExecutor = new ParallelExecutor(maxConcurrency: 10);
    await _parallelExecutor.StartAsync();

    _serialExecutor = new SerialExecutor();
    await _serialExecutor.StartAsync();

    var message = new TestCommand("test-123", 42);
    _envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = []
    };
    _envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Benchmark",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });

    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();

    // Regular context (allocated once)
    _context = new PolicyContext(
      message: message,
      envelope: _envelope,
      services: serviceProvider,
      environment: BENCHMARK_ENVIRONMENT
    );

    // Pooled context (rented each time)
    _pooledContext = PolicyContextPool.Rent(
      message: message,
      envelope: _envelope,
      services: serviceProvider,
      environment: BENCHMARK_ENVIRONMENT
    );

    // Pre-allocate handler to avoid lambda allocation on each call
    _handler = static (env, ctx) => ValueTask.FromResult(((TestCommand)ctx.Message).Value);
  }

  [GlobalCleanup]
  public async Task CleanupAsync() {
    await _parallelExecutor.StopAsync();
    await _serialExecutor.StopAsync();
    PolicyContextPool.Return(_pooledContext);
  }

  /// <summary>
  /// Baseline: ParallelExecutor with inline lambda (current implementation)
  /// </summary>
  [Benchmark(Baseline = true)]
  public async Task<int> ParallelExecutor_InlineLambdaAsync() {
    return await _parallelExecutor.ExecuteAsync<int>(
      _envelope,
      (env, ctx) => ValueTask.FromResult(((TestCommand)ctx.Message).Value),
      _context
    );
  }

  /// <summary>
  /// Test: ParallelExecutor with pre-allocated static handler
  /// </summary>
  [Benchmark]
  public async Task<int> ParallelExecutor_PreAllocatedHandlerAsync() {
    return await _parallelExecutor.ExecuteAsync<int>(
      _envelope,
      _handler,
      _context
    );
  }

  /// <summary>
  /// Test: ParallelExecutor with pooled context
  /// </summary>
  [Benchmark]
  public async Task<int> ParallelExecutor_PooledContextAsync() {
    var pooled = PolicyContextPool.Rent(
      ((TestCommand)_context.Message),
      _envelope,
      _context.Services,
      _context.Environment
    );
    try {
      return await _parallelExecutor.ExecuteAsync<int>(
        _envelope,
        _handler,
        pooled
      );
    } finally {
      PolicyContextPool.Return(pooled);
    }
  }

  /// <summary>
  /// Test: Direct ValueTask.FromResult (no executor overhead)
  /// </summary>
  [Benchmark]
  public ValueTask<int> DirectValueTask() {
    return ValueTask.FromResult(((TestCommand)_context.Message).Value);
  }

  /// <summary>
  /// Test: Direct async/await without executor
  /// </summary>
  [Benchmark]
  public async ValueTask<int> DirectAsyncAwaitAsync() {
    return await ValueTask.FromResult(((TestCommand)_context.Message).Value);
  }

  /// <summary>
  /// Test: Semaphore acquire + release only
  /// </summary>
  [Benchmark]
  public static int SemaphoreAcquireRelease() {
    var sem = new SemaphoreSlim(10, 10);
    sem.Wait(0);
    sem.Release();
    return 42;
  }

  /// <summary>
  /// Test: Lock + semaphore + handler call
  /// </summary>
  [Benchmark]
  public int LockAndSemaphore() {
    var lockObj = new object();
    var sem = new SemaphoreSlim(10, 10);

    lock (lockObj) {
      // Check state
    }

    sem.Wait(0);
    var result = ((TestCommand)_context.Message).Value;
    sem.Release();
    return result;
  }

  /// <summary>
  /// SerialExecutor: Baseline with inline lambda
  /// </summary>
  [Benchmark]
  public async Task<int> SerialExecutor_InlineLambdaAsync() {
    return await _serialExecutor.ExecuteAsync<int>(
      _envelope,
      (env, ctx) => ValueTask.FromResult(((TestCommand)ctx.Message).Value),
      _context
    );
  }

  /// <summary>
  /// SerialExecutor: Pre-allocated handler
  /// </summary>
  [Benchmark]
  public async Task<int> SerialExecutor_PreAllocatedHandlerAsync() {
    return await _serialExecutor.ExecuteAsync<int>(
      _envelope,
      _handler,
      _context
    );
  }

  /// <summary>
  /// SerialExecutor: Synchronous call to avoid benchmark async overhead
  /// </summary>
  [Benchmark]
  public int SerialExecutor_Sync() {
    return _serialExecutor.ExecuteAsync<int>(
      _envelope,
      _handler,
      _context
    ).GetAwaiter().GetResult();
  }
}
