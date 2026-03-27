using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// Integration tests verifying that singleton strategies (Interval/Batch)
/// correctly pass outbox work to the WorkChannelWriter when resolved via the
/// DI registration pattern used by the generator template.
/// </summary>
[Category("Integration")]
public class WorkCoordinatorStrategyChannelIntegrationTests {

  [Test]
  public async Task IntervalStrategy_EndToEnd_OutboxWorkReachesChannelAsync() {
    // Arrange - Full DI container with real WorkChannelWriter
    var options = new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Interval,
      IntervalMilliseconds = 60_000 // long interval, we flush manually
    };
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddSingleton<IServiceInstanceProvider, ChannelTestInstanceProvider>();
    services.AddSingleton<IWorkChannelWriter, WorkChannelWriter>();
    services.AddScoped<IWorkCoordinator, ChannelTestWorkCoordinator>();

    _addGeneratorStrategyRegistrations(services);

    await using var sp = services.BuildServiceProvider();

    // Act - Resolve via scoped pattern (as generator does), queue + flush
    await using var scope = sp.CreateAsyncScope();
    var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

    // Unwrap NonDisposingStrategyAdapter to queue on the actual strategy
    var intervalStrategy = sp.GetRequiredService<IntervalWorkCoordinatorStrategy>();
    intervalStrategy.QueueOutboxMessage(_createTestOutboxMessage());
    await intervalStrategy.FlushAsync(WorkBatchOptions.None);

    // Assert - Read from the channel reader
    var writer = sp.GetRequiredService<IWorkChannelWriter>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var work = await writer.Reader.ReadAsync(cts.Token);

    await Assert.That(work.Destination).IsEqualTo("test-topic");

    // Cleanup
    await intervalStrategy.DisposeAsync();
  }

  [Test]
  public async Task BatchStrategy_EndToEnd_OutboxWorkReachesChannelAsync() {
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

    // Act
    var batchStrategy = sp.GetRequiredService<BatchWorkCoordinatorStrategy>();
    batchStrategy.QueueOutboxMessage(_createTestOutboxMessage());
    await batchStrategy.FlushAsync(WorkBatchOptions.None);

    // Assert
    var writer = sp.GetRequiredService<IWorkChannelWriter>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var work = await writer.Reader.ReadAsync(cts.Token);

    await Assert.That(work.Destination).IsEqualTo("test-topic");

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
        lifecycleMetrics: sp.GetService<LifecycleMetrics>()
,
        workChannelWriter: sp.GetService<IWorkChannelWriter>());
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
        lifecycleMetrics: sp.GetService<LifecycleMetrics>()
,
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
      Hops = []
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

  private sealed class ChannelTestWorkCoordinator : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      var messageId = Guid.CreateVersion7();
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = []
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
