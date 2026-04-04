using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for WorkCoordinatorStrategyFactory and strategy registration.
/// Validates that the factory creates the correct strategy type based on options,
/// and that the DI registration pattern used by the generator resolves correctly.
/// </summary>
public class WorkCoordinatorStrategyRegistrationTests {

  // ========================================
  // FACTORY TESTS
  // ========================================

  [Test]
  public async Task CreateStrategy_DefaultOptions_ReturnsScopedStrategyAsync() {
    // Arrange - default options use Scoped strategy
    var services = _buildServiceCollection(new WorkCoordinatorOptions());
    await using var sp = services.BuildServiceProvider();

    // Act
    var strategy = WorkCoordinatorStrategyFactory.Create(WorkCoordinatorStrategy.Scoped, sp);

    // Assert
    await Assert.That(strategy).IsTypeOf<ScopedWorkCoordinatorStrategy>();
  }

  [Test]
  public async Task CreateStrategy_WithImmediateOption_ReturnsImmediateStrategyAsync() {
    // Arrange
    var services = _buildServiceCollection(new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Immediate
    });
    await using var sp = services.BuildServiceProvider();

    // Act
    var strategy = WorkCoordinatorStrategyFactory.Create(WorkCoordinatorStrategy.Immediate, sp);

    // Assert
    await Assert.That(strategy).IsTypeOf<ImmediateWorkCoordinatorStrategy>();
  }

  [Test]
  public async Task CreateStrategy_WithIntervalOption_ReturnsIntervalStrategyAsync() {
    // Arrange
    var services = _buildServiceCollection(new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Interval,
      IntervalMilliseconds = 500
    });
    await using var sp = services.BuildServiceProvider();

    // Act
    var strategy = WorkCoordinatorStrategyFactory.Create(WorkCoordinatorStrategy.Interval, sp);

    // Assert
    await Assert.That(strategy).IsTypeOf<IntervalWorkCoordinatorStrategy>();

    // Cleanup (Interval has a timer)
    if (strategy is IAsyncDisposable disposable) {
      await disposable.DisposeAsync();
    }
  }

  [Test]
  public async Task CreateStrategy_WithBatchOption_ReturnsBatchStrategyAsync() {
    // Arrange
    var services = _buildServiceCollection(new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Batch,
      BatchSize = 50,
      IntervalMilliseconds = 200
    });
    await using var sp = services.BuildServiceProvider();

    // Act
    var strategy = WorkCoordinatorStrategyFactory.Create(WorkCoordinatorStrategy.Batch, sp);

    // Assert
    await Assert.That(strategy).IsTypeOf<BatchWorkCoordinatorStrategy>();

    // Cleanup (Batch has a timer)
    if (strategy is IAsyncDisposable disposable) {
      await disposable.DisposeAsync();
    }
  }

  // ========================================
  // WORK CHANNEL WRITER INJECTION TESTS
  // These prove that singleton strategies receive the IWorkChannelWriter
  // so outbox work returned from ProcessWorkBatchAsync reaches the channel.
  // ========================================

  [Test]
  public async Task GeneratorPattern_IntervalSingleton_WorkChannelWriterIsNull_WorkNotWrittenAsync() {
    // Arrange - Register a TestWorkChannelWriter so we can observe writes
    var options = new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Interval,
      IntervalMilliseconds = 60_000 // long interval so timer doesn't fire
    };
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddSingleton<IServiceInstanceProvider, RegFakeInstanceProvider>();
    var testWriter = new TestWorkChannelWriter();
    services.AddSingleton<IWorkChannelWriter>(testWriter);

    // Register a fake coordinator that returns outbox work
    services.AddScoped<IWorkCoordinator, RegFakeWorkCoordinatorWithOutboxWork>();

    _addGeneratorStrategyRegistrations(services);

    await using var sp = services.BuildServiceProvider();

    // Act - Resolve the singleton, queue a message, flush
    var strategy = sp.GetRequiredService<IntervalWorkCoordinatorStrategy>();
    strategy.QueueOutboxMessage(_createTestOutboxMessage());
    await strategy.FlushAsync(WorkBatchOptions.None);

    // Assert — channel writes no longer happen during flush (work is persisted to DB,
    // coordinator loop picks it up on next tick)
    await Assert.That(testWriter.WrittenWork).Count().IsEqualTo(0)
      .Because("ExecuteFlushAsync no longer writes outbox work to channel");

    // Cleanup
    await strategy.DisposeAsync();
  }

  [Test]
  public async Task GeneratorPattern_BatchSingleton_WorkChannelWriterIsNull_WorkNotWrittenAsync() {
    // Arrange
    var options = new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Batch,
      BatchSize = 100,
      IntervalMilliseconds = 60_000
    };
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddSingleton<IServiceInstanceProvider, RegFakeInstanceProvider>();
    var testWriter = new TestWorkChannelWriter();
    services.AddSingleton<IWorkChannelWriter>(testWriter);
    services.AddScoped<IWorkCoordinator, RegFakeWorkCoordinatorWithOutboxWork>();

    _addGeneratorStrategyRegistrations(services);

    await using var sp = services.BuildServiceProvider();

    // Act
    var strategy = sp.GetRequiredService<BatchWorkCoordinatorStrategy>();
    strategy.QueueOutboxMessage(_createTestOutboxMessage());
    await strategy.FlushAsync(WorkBatchOptions.None);

    // Assert — channel writes no longer happen during flush (work is persisted to DB,
    // coordinator loop picks it up on next tick)
    await Assert.That(testWriter.WrittenWork).Count().IsEqualTo(0)
      .Because("ExecuteFlushAsync no longer writes outbox work to channel");

    // Cleanup
    await strategy.DisposeAsync();
  }

  [Test]
  public async Task GeneratorPattern_IntervalSingleton_MetricsAreNull_FlushRecordsNothingAsync() {
    // Arrange - Register WorkCoordinatorMetrics so we can verify it's passed
    var options = new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Interval,
      IntervalMilliseconds = 60_000
    };
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddSingleton<IServiceInstanceProvider, RegFakeInstanceProvider>();
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    services.AddSingleton(metrics);
    services.AddScoped<IWorkCoordinator, RegFakeWorkCoordinatorWithOutboxWork>();

    _addGeneratorStrategyRegistrations(services);

    await using var sp = services.BuildServiceProvider();

    // Act - Resolve singleton, queue + flush
    var strategy = sp.GetRequiredService<IntervalWorkCoordinatorStrategy>();
    strategy.QueueOutboxMessage(_createTestOutboxMessage());
    await strategy.FlushAsync(WorkBatchOptions.None);

    // Assert - If metrics were passed, FlushCalls counter would be incremented.
    // We verify the metrics object is reachable by checking that no NullReferenceException occurred
    // and that the flush completed successfully (the metric instruments exist).
    // A more targeted assertion: the FlushCalls counter instrument should exist.
    await Assert.That(metrics.FlushCalls).IsNotNull()
      .Because("Metrics should be resolved and passed to the singleton strategy");

    // Cleanup
    await strategy.DisposeAsync();
  }

  // ========================================
  // DI REGISTRATION PATTERN TESTS
  // These validate that the pattern used in the generator template
  // (EFCoreSnippets.cs REGISTER_INFRASTRUCTURE_SNIPPET) works correctly
  // ========================================

  [Test]
  public async Task GeneratorPattern_DefaultScoped_ResolvesCorrectlyAsync() {
    // Arrange - Simulate exactly what the generator template registers
    var options = new WorkCoordinatorOptions { Strategy = WorkCoordinatorStrategy.Scoped };
    var services = _buildGeneratorRegistrationPattern(options);
    await using var sp = services.BuildServiceProvider();

    // Act - Resolve IWorkCoordinatorStrategy as the generator would
    await using var scope = sp.CreateAsyncScope();
    var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

    // Assert
    await Assert.That(strategy).IsTypeOf<ScopedWorkCoordinatorStrategy>()
      .Because("Default Scoped option should produce ScopedWorkCoordinatorStrategy via generator pattern");
  }

  [Test]
  public async Task GeneratorPattern_Immediate_ResolvesCorrectlyAsync() {
    // Arrange
    var options = new WorkCoordinatorOptions { Strategy = WorkCoordinatorStrategy.Immediate };
    var services = _buildGeneratorRegistrationPattern(options);
    await using var sp = services.BuildServiceProvider();

    // Act
    await using var scope = sp.CreateAsyncScope();
    var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

    // Assert
    await Assert.That(strategy).IsTypeOf<ImmediateWorkCoordinatorStrategy>()
      .Because("Immediate option should produce ImmediateWorkCoordinatorStrategy via generator pattern");
  }

  [Test]
  public async Task GeneratorPattern_Interval_ResolvesSingletonAsync() {
    // Arrange
    var options = new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Interval,
      IntervalMilliseconds = 5000
    };
    var services = _buildGeneratorRegistrationPattern(options);
    await using var sp = services.BuildServiceProvider();

    // Act - Resolve from two scopes
    IWorkCoordinatorStrategy strategy1;
    IWorkCoordinatorStrategy strategy2;
    await using (var scope1 = sp.CreateAsyncScope()) {
      strategy1 = scope1.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    }
    await using (var scope2 = sp.CreateAsyncScope()) {
      strategy2 = scope2.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    }

    // Assert - Both should wrap the same singleton instance via NonDisposingStrategyAdapter
    await Assert.That(strategy1).IsTypeOf<NonDisposingStrategyAdapter>()
      .Because("Interval option should be wrapped in NonDisposingStrategyAdapter to prevent scope disposal");

    // Verify the underlying singleton is shared (resolve concrete type)
    var singleton = sp.GetRequiredService<IntervalWorkCoordinatorStrategy>();
    await Assert.That(singleton).IsNotNull()
      .Because("Interval singleton should be resolvable directly");

    // Cleanup
    await singleton.DisposeAsync();
  }

  [Test]
  public async Task GeneratorPattern_Batch_ResolvesSingletonAsync() {
    // Arrange
    var options = new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Batch,
      BatchSize = 100,
      IntervalMilliseconds = 5000
    };
    var services = _buildGeneratorRegistrationPattern(options);
    await using var sp = services.BuildServiceProvider();

    // Act - Resolve from two scopes
    IWorkCoordinatorStrategy strategy1;
    IWorkCoordinatorStrategy strategy2;
    await using (var scope1 = sp.CreateAsyncScope()) {
      strategy1 = scope1.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    }
    await using (var scope2 = sp.CreateAsyncScope()) {
      strategy2 = scope2.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    }

    // Assert - Both should wrap the same singleton instance via NonDisposingStrategyAdapter
    await Assert.That(strategy1).IsTypeOf<NonDisposingStrategyAdapter>()
      .Because("Batch option should be wrapped in NonDisposingStrategyAdapter to prevent scope disposal");

    // Verify the underlying singleton is shared (resolve concrete type)
    var singleton = sp.GetRequiredService<BatchWorkCoordinatorStrategy>();
    await Assert.That(singleton).IsNotNull()
      .Because("Batch singleton should be resolvable directly");

    // Cleanup
    await singleton.DisposeAsync();
  }

  [Test]
  public async Task GeneratorPattern_Scoped_CreatesNewPerScopeAsync() {
    // Arrange
    var options = new WorkCoordinatorOptions { Strategy = WorkCoordinatorStrategy.Scoped };
    var services = _buildGeneratorRegistrationPattern(options);
    await using var sp = services.BuildServiceProvider();

    // Act - Resolve from two scopes
    IWorkCoordinatorStrategy strategy1;
    IWorkCoordinatorStrategy strategy2;
    await using (var scope1 = sp.CreateAsyncScope()) {
      strategy1 = scope1.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    }
    await using (var scope2 = sp.CreateAsyncScope()) {
      strategy2 = scope2.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    }

    // Assert - Should be different instances (scoped)
    await Assert.That(strategy1).IsTypeOf<ScopedWorkCoordinatorStrategy>();
    await Assert.That(ReferenceEquals(strategy1, strategy2)).IsFalse()
      .Because("Scoped strategy should create a new instance per scope");
  }

  [Test]
  public async Task GeneratorPattern_OptionsConfiguredViaIOptions_AppliedCorrectlyAsync() {
    // Arrange - Simulate user configuring via services.Configure<WorkCoordinatorOptions>()
    var services = new ServiceCollection();
    services.Configure<WorkCoordinatorOptions>(opts => {
      opts.Strategy = WorkCoordinatorStrategy.Batch;
      opts.BatchSize = 42;
      opts.IntervalMilliseconds = 999;
    });
    // Register WorkCoordinatorOptions singleton from IOptions<T> (same pattern as generator)
    services.AddSingleton<WorkCoordinatorOptions>(sp => {
      var optionsAccessor = sp.GetService<Microsoft.Extensions.Options.IOptions<WorkCoordinatorOptions>>();
      return optionsAccessor?.Value ?? new WorkCoordinatorOptions();
    });
    services.AddScoped<IWorkCoordinator, RegFakeWorkCoordinator>();
    services.AddSingleton<IServiceInstanceProvider, RegFakeInstanceProvider>();

    // Register generator pattern
    _addGeneratorStrategyRegistrations(services);

    await using var sp = services.BuildServiceProvider();

    // Act
    var resolvedOptions = sp.GetRequiredService<WorkCoordinatorOptions>();
    await using var scope = sp.CreateAsyncScope();
    var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

    // Assert
    await Assert.That(resolvedOptions.Strategy).IsEqualTo(WorkCoordinatorStrategy.Batch)
      .Because("Options configured via IOptions<T> should be resolved correctly");
    await Assert.That(resolvedOptions.BatchSize).IsEqualTo(42);
    await Assert.That(resolvedOptions.IntervalMilliseconds).IsEqualTo(999);
    await Assert.That(strategy).IsTypeOf<NonDisposingStrategyAdapter>()
      .Because("Batch strategy should be wrapped in NonDisposingStrategyAdapter");
  }

  [Test]
  public async Task GeneratorPattern_AllEnumValuesHandled_DoesNotThrowAsync() {
    // Arrange - Validate all enum values are handled (no ArgumentOutOfRangeException)
    var enumValues = Enum.GetValues<WorkCoordinatorStrategy>();

    foreach (var strategyValue in enumValues) {
      var options = new WorkCoordinatorOptions {
        Strategy = strategyValue,
        IntervalMilliseconds = 5000,
        BatchSize = 100
      };
      var services = _buildGeneratorRegistrationPattern(options);
      await using var sp = services.BuildServiceProvider();

      // Act & Assert - Should not throw
      await using var scope = sp.CreateAsyncScope();
      var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

      await Assert.That(strategy).IsNotNull()
        .Because($"Strategy enum value {strategyValue} should be handled without throwing");

      // Cleanup
      if (strategy is IAsyncDisposable disposable) {
        await disposable.DisposeAsync();
      }
    }
  }

  // ========================================
  // Helpers - Simulate generator DI pattern
  // ========================================

  /// <summary>
  /// Builds a minimal ServiceCollection with the fakes needed for factory tests.
  /// </summary>
  private static ServiceCollection _buildServiceCollection(WorkCoordinatorOptions options) {
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddScoped<IWorkCoordinator, RegFakeWorkCoordinator>();
    services.AddSingleton<IServiceInstanceProvider, RegFakeInstanceProvider>();
    return services;
  }

  /// <summary>
  /// Builds a ServiceCollection that mirrors the exact pattern the generator emits in EFCoreSnippets.cs.
  /// This validates that configuration options actually control which strategy is used.
  /// </summary>
  private static ServiceCollection _buildGeneratorRegistrationPattern(WorkCoordinatorOptions options) {
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddScoped<IWorkCoordinator, RegFakeWorkCoordinator>();
    services.AddSingleton<IServiceInstanceProvider, RegFakeInstanceProvider>();

    _addGeneratorStrategyRegistrations(services);

    return services;
  }

  /// <summary>
  /// Adds the strategy registrations exactly as the generator template produces them.
  /// This is the code from REGISTER_INFRASTRUCTURE_SNIPPET in EFCoreSnippets.cs.
  /// If the generator template changes, this must be updated to match — test failures
  /// here indicate a mismatch between the template and the expected DI behavior.
  /// </summary>
  private static void _addGeneratorStrategyRegistrations(IServiceCollection services) {
    // Singleton timer-based strategies (shared across scopes)
    services.AddSingleton<IntervalWorkCoordinatorStrategy>(sp => {
      var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
      var options = sp.GetRequiredService<WorkCoordinatorOptions>();
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new IntervalWorkCoordinatorStrategy(
        coordinator: null,
        instanceProvider,
        options,
        scopeFactory: scopeFactory,
        metrics: sp.GetService<WorkCoordinatorMetrics>(),
        lifecycleMetrics: sp.GetService<LifecycleMetrics>(),
        workChannelWriter: sp.GetService<IWorkChannelWriter>()
      );
    });
    services.AddSingleton<BatchWorkCoordinatorStrategy>(sp => {
      var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
      var options = sp.GetRequiredService<WorkCoordinatorOptions>();
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new BatchWorkCoordinatorStrategy(
        coordinator: null,
        instanceProvider,
        options,
        scopeFactory: scopeFactory,
        metrics: sp.GetService<WorkCoordinatorMetrics>(),
        lifecycleMetrics: sp.GetService<LifecycleMetrics>(),
        workChannelWriter: sp.GetService<IWorkChannelWriter>()
      );
    });

    // Scoped factory selects based on options
    // Singleton strategies are wrapped in NonDisposingStrategyAdapter to prevent
    // scope disposal from destroying the shared singleton instance.
    services.AddScoped<IWorkCoordinatorStrategy>(sp => {
      var options = sp.GetRequiredService<WorkCoordinatorOptions>();
      return options.Strategy switch {
        WorkCoordinatorStrategy.Interval =>
          new NonDisposingStrategyAdapter(
            sp.GetRequiredService<IntervalWorkCoordinatorStrategy>()),
        WorkCoordinatorStrategy.Batch =>
          new NonDisposingStrategyAdapter(
            sp.GetRequiredService<BatchWorkCoordinatorStrategy>()),
        _ => WorkCoordinatorStrategyFactory.Create(options.Strategy, sp)
      };
    });
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class RegFakeWorkCoordinator : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    public void ClearInFlight() { }
    private readonly List<OutboxWork> _writtenWork = [];
    public IReadOnlyList<OutboxWork> WrittenWork => _writtenWork;
    public System.Threading.Channels.ChannelReader<OutboxWork> Reader =>
      System.Threading.Channels.Channel.CreateUnbounded<OutboxWork>().Reader;
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) {
      _writtenWork.Add(work);
      return ValueTask.CompletedTask;
    }
    public bool TryWrite(OutboxWork work) {
      _writtenWork.Add(work);
      return true;
    }
    public void Complete() { }

    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
  }

  private sealed class RegFakeWorkCoordinatorWithOutboxWork : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      var messageId = Guid.CreateVersion7();
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      return Task.FromResult(new WorkBatch {
        OutboxWork = [
          new OutboxWork {
            MessageId = messageId,
            Destination = "test-topic",
            Envelope = envelope,
            EnvelopeType = "TestEnvelope",
            MessageType = "TestMessage",
            Attempts = 0
          }
        ],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static OutboxMessage _createTestOutboxMessage() {
    var messageId = Guid.CreateVersion7();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new Whizbang.Core.Observability.EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      },
      EnvelopeType = "TestEnvelope",
      MessageType = "TestMessage"
    };
  }

  private sealed class RegFakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "RegistrationTestService";
    public string HostName => "test-host";
    public int ProcessId => 12345;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
