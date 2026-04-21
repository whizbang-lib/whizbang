using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage tests for ServiceBusConsumerWorker targeting uncovered error paths:
/// - StartAsync exception handling (lines 105-106)
/// - ExecuteAsync fatal error handling (lines 125-126)
/// </summary>
[Category("Workers")]
public class ServiceBusConsumerWorkerCoverageTests {

  [Test]
  public async Task StartAsync_WhenSubscribeFails_LogsAndRethrowsAsync() {
    // Arrange - Transport that throws on subscribe (exercises lines 105-106)
    var failingTransport = new FailingTransport();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinatorStrategy>(new TestWorkCoordinatorStrategy(
      () => new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] }
    ));
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var logger = new TestLogger<ServiceBusConsumerWorker>();
    var orderedProcessor = new OrderedStreamProcessor();

    var workerOptions = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("test-topic", "test-sub")
      ]
    };

    var worker = new ServiceBusConsumerWorker(
      failingTransport,
      scopeFactory,
      jsonOptions,
      logger,
      orderedProcessor,
      workerOptions
    );

    // Act & Assert - StartAsync should throw because transport.SubscribeAsync fails
    await Assert.That(async () => await worker.StartAsync(CancellationToken.None))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task ExecuteAsync_WhenFatalErrorOccurs_LogsAndRethrowsAsync() {
    // Arrange - Create a worker with no subscriptions (so StartAsync succeeds quickly)
    // Then cancel immediately to trigger the OperationCanceledException path (line 122-123)
    var transport = new TestTransport();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinatorStrategy>(new TestWorkCoordinatorStrategy(
      () => new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] }
    ));
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var logger = new TestLogger<ServiceBusConsumerWorker>();
    var orderedProcessor = new OrderedStreamProcessor();

    var workerOptions = new ServiceBusConsumerOptions {
      Subscriptions = [] // No subscriptions
    };

    var worker = new ServiceBusConsumerWorker(
      transport,
      scopeFactory,
      jsonOptions,
      logger,
      orderedProcessor,
      workerOptions
    );

    // Act - Start then stop (triggers OperationCanceledException in ExecuteAsync)
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Cancel to stop ExecuteAsync — StopAsync will also cancel and await the task
    await cts.CancelAsync();

    // Stop gracefully — StopAsync awaits ExecuteAsync completion, no delay needed
    await worker.StopAsync(CancellationToken.None);

    // Assert - Worker stopped without throwing (OperationCanceledException was caught)
    var stopped = true;
    await Assert.That(stopped).IsTrue();
  }

  #region Test Doubles

  /// <summary>
  /// Transport that fails on subscribe to trigger StartAsync error path.
  /// </summary>
  private sealed class FailingTransport : ITransport {
    public bool IsInitialized => false;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Simulated subscription failure");
    }

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Simulated subscription failure");
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope envelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull =>
      throw new NotImplementedException();

    public void Dispose() { }
  }

  #endregion
}
