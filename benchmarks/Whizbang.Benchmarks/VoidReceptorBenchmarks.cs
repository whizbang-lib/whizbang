using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks comparing void receptors (IReceptor&lt;TMessage&gt;) vs regular receptors (IReceptor&lt;TMessage, TResponse&gt;).
///
/// MEASUREMENT SCOPE: Framework overhead ONLY
/// - Commands/events pre-allocated in GlobalSetup (excluded from measurement)
/// - Measures ONLY dispatcher and receptor invocation overhead
/// - Application allocation cost (command creation) not included
///
/// TARGET: Minimize framework-only allocation for void receptor pattern.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class VoidReceptorBenchmarks {
  private IDispatcher _dispatcherWithTracing = null!;
  private IDispatcher _dispatcherNoTracing = null!;

  // Pre-allocated commands (framework-only measurement)
  private ProcessCommand _processCommand = null!;
  private CreateOrderCommand _orderCommand = null!;
  private ProcessCommand[] _batchProcessCommands = null!;
  private CreateOrderCommand[] _batchOrderCommands = null!;

  // Test commands
  public record ProcessCommand(int Id, string Action);
  public record CreateOrderCommand(Guid CustomerId, decimal Amount);

  // Void receptor (zero allocations target)
  public class ProcessCommandReceptor : IReceptor<ProcessCommand> {
    public int ProcessedCount { get; private set; }

    public ValueTask HandleAsync(ProcessCommand message, CancellationToken cancellationToken = default) {
      ProcessedCount++;
      // Synchronous completion - zero allocation!
      return ValueTask.CompletedTask;
    }
  }

  // Void receptor with async work (for comparison)
  public class AsyncProcessCommandReceptor : IReceptor<ProcessCommand> {
    public int ProcessedCount { get; private set; }

    public async ValueTask HandleAsync(ProcessCommand message, CancellationToken cancellationToken = default) {
      await Task.Yield();
      ProcessedCount++;
    }
  }

  // Regular receptor with response (baseline)
  public class CreateOrderReceptor : IReceptor<CreateOrderCommand, OrderCreatedEvent> {
    public ValueTask<OrderCreatedEvent> HandleAsync(
        CreateOrderCommand message,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new OrderCreatedEvent(Guid.NewGuid(), message.CustomerId));
    }
  }

  // Regular receptor with async work (baseline)
  public class AsyncCreateOrderReceptor : IReceptor<CreateOrderCommand, OrderCreatedEvent> {
    public async ValueTask<OrderCreatedEvent> HandleAsync(
        CreateOrderCommand message,
        CancellationToken cancellationToken = default) {
      await Task.Yield();
      return new OrderCreatedEvent(Guid.NewGuid(), message.CustomerId);
    }
  }

  public record OrderCreatedEvent(Guid OrderId, Guid CustomerId);

  [GlobalSetup]
  public void Setup() {
    // Dispatcher WITH tracing (slower path)
    var servicesWithTracing = new ServiceCollection();
    servicesWithTracing.AddTransient<IReceptor<ProcessCommand>, ProcessCommandReceptor>();
    servicesWithTracing.AddTransient<IReceptor<ProcessCommand>, AsyncProcessCommandReceptor>();
    servicesWithTracing.AddTransient<IReceptor<CreateOrderCommand, OrderCreatedEvent>, CreateOrderReceptor>();
    servicesWithTracing.AddTransient<IReceptor<CreateOrderCommand, OrderCreatedEvent>, AsyncCreateOrderReceptor>();
    var providerWithTracing = servicesWithTracing.BuildServiceProvider();
    _dispatcherWithTracing = new Benchmarks.Generated.GeneratedDispatcher(
      providerWithTracing,
      new ServiceInstanceProvider(Guid.NewGuid(), "BenchmarkService", "benchmark-host", 12345),
      new InMemoryTraceStore(),
      null,  // transport
      null   // jsonOptions
    );

    // Dispatcher WITHOUT tracing (fast path - zero allocations target)
    var servicesNoTracing = new ServiceCollection();
    servicesNoTracing.AddTransient<IReceptor<ProcessCommand>, ProcessCommandReceptor>();
    servicesNoTracing.AddTransient<IReceptor<ProcessCommand>, AsyncProcessCommandReceptor>();
    servicesNoTracing.AddTransient<IReceptor<CreateOrderCommand, OrderCreatedEvent>, CreateOrderReceptor>();
    servicesNoTracing.AddTransient<IReceptor<CreateOrderCommand, OrderCreatedEvent>, AsyncCreateOrderReceptor>();
    var providerNoTracing = servicesNoTracing.BuildServiceProvider();
    _dispatcherNoTracing = new Benchmarks.Generated.GeneratedDispatcher(
      providerNoTracing,
      new ServiceInstanceProvider(Guid.NewGuid(), "BenchmarkService", "benchmark-host", 12345),
      traceStore: null,  // No tracing = fast path
      transport: null,
      jsonOptions: null
    );

    // Pre-allocate commands to measure ONLY framework overhead
    _processCommand = new ProcessCommand(1, "process");
    _orderCommand = new CreateOrderCommand(Guid.NewGuid(), 100.00m);

    // Pre-allocate batch arrays
    _batchProcessCommands = new ProcessCommand[100];
    for (int i = 0; i < 100; i++) {
      _batchProcessCommands[i] = new ProcessCommand(i, "process");
    }

    _batchOrderCommands = new CreateOrderCommand[100];
    for (int i = 0; i < 100; i++) {
      _batchOrderCommands[i] = new CreateOrderCommand(Guid.NewGuid(), i * 10.0m);
    }
  }

  // ============================================================================
  // BASELINE: Regular Receptors with Response
  // ============================================================================

  /// <summary>
  /// Measures ONLY framework overhead using pre-allocated command.
  /// Command allocation excluded via GlobalSetup.
  /// </summary>
  [Benchmark(Baseline = true)]
  public async Task<OrderCreatedEvent> RegularReceptor_SyncHandler_NoTracing() {
    return await _dispatcherNoTracing.LocalInvokeAsync<OrderCreatedEvent>(_orderCommand);
  }

  /// <summary>
  /// Measures framework overhead WITH tracing enabled using pre-allocated command.
  /// </summary>
  [Benchmark]
  public async Task<OrderCreatedEvent> RegularReceptor_SyncHandler_WithTracing() {
    return await _dispatcherWithTracing.LocalInvokeAsync<OrderCreatedEvent>(_orderCommand);
  }

  /// <summary>
  /// Measures framework overhead with async handler using pre-allocated command.
  /// </summary>
  [Benchmark]
  public async Task<OrderCreatedEvent> RegularReceptor_AsyncHandler_NoTracing() {
    return await _dispatcherNoTracing.LocalInvokeAsync<OrderCreatedEvent>(_orderCommand);
  }

  /// <summary>
  /// Measures framework overhead with async handler AND tracing using pre-allocated command.
  /// </summary>
  [Benchmark]
  public async Task<OrderCreatedEvent> RegularReceptor_AsyncHandler_WithTracing() {
    return await _dispatcherWithTracing.LocalInvokeAsync<OrderCreatedEvent>(_orderCommand);
  }

  // ============================================================================
  // ZERO-ALLOCATION TARGET: Void Receptors
  // ============================================================================

  /// <summary>
  /// Measures ONLY framework overhead for void receptors using pre-allocated command.
  /// TARGET: Minimize framework-only allocation.
  /// </summary>
  [Benchmark]
  public async Task VoidReceptor_SyncHandler_NoTracing() {
    await _dispatcherNoTracing.LocalInvokeAsync(_processCommand);
  }

  /// <summary>
  /// Measures framework overhead for void receptors WITH tracing.
  /// </summary>
  [Benchmark]
  public async Task VoidReceptor_SyncHandler_WithTracing() {
    await _dispatcherWithTracing.LocalInvokeAsync(_processCommand);
  }

  /// <summary>
  /// Measures framework overhead for void receptors with async handler.
  /// </summary>
  [Benchmark]
  public async Task VoidReceptor_AsyncHandler_NoTracing() {
    await _dispatcherNoTracing.LocalInvokeAsync(_processCommand);
  }

  /// <summary>
  /// Measures framework overhead for void receptors with async handler AND tracing.
  /// </summary>
  [Benchmark]
  public async Task VoidReceptor_AsyncHandler_WithTracing() {
    await _dispatcherWithTracing.LocalInvokeAsync(_processCommand);
  }

  // ============================================================================
  // THROUGHPUT: Batch Operations
  // ============================================================================

  /// <summary>
  /// Measures framework overhead for 100 void receptor invocations using pre-allocated commands.
  /// Commands allocated in GlobalSetup - measures ONLY framework dispatch overhead.
  /// </summary>
  [Benchmark]
  public async Task VoidReceptor_Batch100_SyncHandler_NoTracing() {
    var tasks = new Task[100];
    for (int i = 0; i < 100; i++) {
      tasks[i] = _dispatcherNoTracing.LocalInvokeAsync(_batchProcessCommands[i]).AsTask();
    }
    await Task.WhenAll(tasks);
  }

  /// <summary>
  /// Measures framework overhead for 100 regular receptor invocations using pre-allocated commands.
  /// Commands allocated in GlobalSetup - measures ONLY framework dispatch overhead.
  /// </summary>
  [Benchmark]
  public async Task RegularReceptor_Batch100_SyncHandler_NoTracing() {
    var tasks = new Task<OrderCreatedEvent>[100];
    for (int i = 0; i < 100; i++) {
      tasks[i] = _dispatcherNoTracing.LocalInvokeAsync<OrderCreatedEvent>(_batchOrderCommands[i]).AsTask();
    }
    await Task.WhenAll(tasks);
  }

  // ============================================================================
  // HELPER: Simple in-memory trace store for benchmarking
  // ============================================================================

  private class InMemoryTraceStore : ITraceStore {
    private readonly List<IMessageEnvelope> _traces = [];

    public Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default) {
      _traces.Add(envelope);
      return Task.CompletedTask;
    }

    public Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default) {
      return Task.FromResult(_traces.FirstOrDefault(t => t.MessageId == messageId));
    }

    public Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default) {
      return Task.FromResult(_traces.Where(t => t.Hops.Any(h => h.CorrelationId == correlationId)).ToList());
    }

    public Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default) {
      // Simplified implementation for benchmarking
      return Task.FromResult(_traces.Where(t => t.MessageId == messageId).ToList());
    }

    public Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) {
      return Task.FromResult(_traces.Where(t =>
        t.Hops.Any(h => h.Timestamp >= from && h.Timestamp <= to)
      ).ToList());
    }
  }
}
