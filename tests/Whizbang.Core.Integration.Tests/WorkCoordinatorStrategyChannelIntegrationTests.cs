using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// Integration tests verifying that singleton strategies (Interval/Batch)
/// correctly signal the WorkChannelWriter and route inbox work to the InboxChannelWriter
/// when resolved via the DI registration pattern used by the generator template.
/// </summary>
/// <docs>messaging/work-coordinator#channel-routing</docs>
/// <tests>tests/Whizbang.Core.Integration.Tests/WorkCoordinatorStrategyChannelIntegrationTests.cs</tests>
[Category("Integration")]
public class WorkCoordinatorStrategyChannelIntegrationTests {

  [Test]
  public async Task IntervalStrategy_FlushWithOutboxWork_SignalsWorkAvailableAsync() {
    // Arrange - Full DI container with real WorkChannelWriter
    var options = new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Interval,
      IntervalMilliseconds = 60_000 // long interval, we flush manually
    };
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddSingleton<IServiceInstanceProvider, ChannelTestInstanceProvider>();
    services.AddSingleton<IWorkChannelWriter, WorkChannelWriter>();
    services.AddSingleton<IInboxChannelWriter, InboxChannelWriter>();
    services.AddScoped<IWorkCoordinator, ChannelTestWorkCoordinator>();

    _addGeneratorStrategyRegistrations(services);

    await using var sp = services.BuildServiceProvider();

    var writer = sp.GetRequiredService<IWorkChannelWriter>();
    var signalFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    writer.OnNewWorkAvailable += () => signalFired.TrySetResult();

    // Act - Queue outbox message and flush.
    // FlushAsync is a no-op on IntervalStrategy (defers to timer); use FlushAndGetBatchAsync
    // for the immediate-flush path the test needs.
    var intervalStrategy = sp.GetRequiredService<IntervalWorkCoordinatorStrategy>();
    intervalStrategy.QueueOutboxMessage(_createTestOutboxMessage());
    _ = await intervalStrategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    // Assert - Signal was raised (publisher worker would wake and claim from DB)
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    cts.Token.Register(() => signalFired.TrySetCanceled());
    await signalFired.Task;

    // Cleanup
    await intervalStrategy.DisposeAsync();
  }

  // Removed: IntervalStrategy_FlushWithInboxWork_RoutesToInboxChannelAsync — tested the legacy
  // flush-claims-and-returns-work behavior. Phase H decoupled claiming from flushing: ClaimWorker
  // claims work asynchronously via claim_work and routes to the inbox channel directly. Strategy
  // flushes only persist new messages now.

  [Test]
  public async Task BatchStrategy_FlushWithOutboxWork_SignalsWorkAvailableAsync() {
    // Arrange
    var options = new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Batch,
      BatchSize = 100,
      IntervalMilliseconds = 60_000
    };
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddSingleton<IServiceInstanceProvider, ChannelTestInstanceProvider>();
    services.AddSingleton<IWorkChannelWriter, WorkChannelWriter>();
    services.AddScoped<IWorkCoordinator, ChannelTestWorkCoordinator>();

    _addGeneratorStrategyRegistrations(services);

    await using var sp = services.BuildServiceProvider();

    var writer = sp.GetRequiredService<IWorkChannelWriter>();
    var signalFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    writer.OnNewWorkAvailable += () => signalFired.TrySetResult();

    // Act — FlushAsync is a no-op on BatchStrategy (waits for debounce/batch-size trigger).
    // Use FlushAndGetBatchAsync for the immediate-flush path the test needs.
    var batchStrategy = sp.GetRequiredService<BatchWorkCoordinatorStrategy>();
    batchStrategy.QueueOutboxMessage(_createTestOutboxMessage());
    _ = await batchStrategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    // Assert - Signal was raised
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    cts.Token.Register(() => signalFired.TrySetCanceled());
    await signalFired.Task;

    // Cleanup
    await batchStrategy.DisposeAsync();
  }

  // ========================================
  // Helpers
  // ========================================

  private static void _addGeneratorStrategyRegistrations(IServiceCollection services) {
    // Mirrors the generator template pattern from EFCoreSnippets.cs
    services.AddSingleton<IntervalWorkCoordinatorStrategy>(sp => {
      var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
      var opts = sp.GetRequiredService<WorkCoordinatorOptions>();
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new IntervalWorkCoordinatorStrategy(
        coordinator: null,
        instanceProvider,
        opts,
        scopeFactory: scopeFactory,
        metrics: sp.GetService<WorkCoordinatorMetrics>(),
        lifecycleMetrics: sp.GetService<LifecycleMetrics>(),
        workChannelWriter: sp.GetService<IWorkChannelWriter>(),
        inboxChannelWriter: sp.GetService<IInboxChannelWriter>());
    });
    services.AddSingleton<BatchWorkCoordinatorStrategy>(sp => {
      var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
      var opts = sp.GetRequiredService<WorkCoordinatorOptions>();
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new BatchWorkCoordinatorStrategy(
        coordinator: null,
        instanceProvider,
        opts,
        scopeFactory: scopeFactory,
        metrics: sp.GetService<WorkCoordinatorMetrics>(),
        lifecycleMetrics: sp.GetService<LifecycleMetrics>(),
        workChannelWriter: sp.GetService<IWorkChannelWriter>());
    });

    services.AddScoped<IWorkCoordinatorStrategy>(sp => {
      var opts = sp.GetRequiredService<WorkCoordinatorOptions>();
      return opts.Strategy switch {
        WorkCoordinatorStrategy.Interval =>
          new NonDisposingStrategyAdapter(
            sp.GetRequiredService<IntervalWorkCoordinatorStrategy>()),
        WorkCoordinatorStrategy.Batch =>
          new NonDisposingStrategyAdapter(
            sp.GetRequiredService<BatchWorkCoordinatorStrategy>()),
        _ => WorkCoordinatorStrategyFactory.Create(opts.Strategy, sp)
      };
    });
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
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      },
      EnvelopeType = "TestEnvelope",
      MessageType = "TestMessage"
    };
  }

  // ========================================
  // Test Fakes
  // ========================================

  /// <summary>Returns outbox work (no inbox work) from ProcessWorkBatchAsync.</summary>
  private sealed class ChannelTestWorkCoordinator : IWorkCoordinator {
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

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>Returns inbox work from ProcessWorkBatchAsync (for inbox channel routing tests).</summary>
  private sealed class InboxReturningWorkCoordinator : IWorkCoordinator {
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
        OutboxWork = [],
        InboxWork = [
          new InboxWork {
            MessageId = messageId,
            Envelope = envelope,
            MessageType = "TestMessage",
            StreamId = Guid.CreateVersion7(),
            Status = MessageProcessingStatus.None
          }
        ],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class ChannelTestInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "ChannelIntegrationTestService";
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
