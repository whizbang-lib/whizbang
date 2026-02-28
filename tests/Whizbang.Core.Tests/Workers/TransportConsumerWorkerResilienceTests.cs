using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for TransportConsumerWorker subscription resilience - verifies retry logic,
/// exponential backoff, and recovery handling.
/// </summary>
/// <tests>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</tests>
public class TransportConsumerWorkerResilienceTests {
  #region CalculateNextDelay Tests

  [Test]
  public async Task CalculateNextDelay_WithBackoffMultiplier2_DoublesDelayAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(120)
    };
    var currentDelay = TimeSpan.FromSeconds(1);

    // Act
    var nextDelay = SubscriptionRetryHelper.CalculateNextDelay(currentDelay, options);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task CalculateNextDelay_WhenExceedsMax_CapsAtMaxDelayAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(120)
    };
    var currentDelay = TimeSpan.FromSeconds(100);

    // Act
    var nextDelay = SubscriptionRetryHelper.CalculateNextDelay(currentDelay, options);

    // Assert - 100 * 2 = 200, but capped at 120
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(120));
  }

  [Test]
  public async Task CalculateNextDelay_WithMultiplier1_MaintainsConstantDelayAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      BackoffMultiplier = 1.0,
      MaxRetryDelay = TimeSpan.FromSeconds(120)
    };
    var currentDelay = TimeSpan.FromSeconds(5);

    // Act
    var nextDelay = SubscriptionRetryHelper.CalculateNextDelay(currentDelay, options);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(5));
  }

  [Test]
  public async Task CalculateNextDelay_ExponentialSequence_FollowsPatternAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      InitialRetryDelay = TimeSpan.FromSeconds(1),
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(120)
    };

    // Act & Assert - verify exponential sequence 1, 2, 4, 8, 16, 32, 64, 120 (capped)
    var delay = options.InitialRetryDelay;
    await Assert.That(delay.TotalSeconds).IsEqualTo(1);

    delay = SubscriptionRetryHelper.CalculateNextDelay(delay, options);
    await Assert.That(delay.TotalSeconds).IsEqualTo(2);

    delay = SubscriptionRetryHelper.CalculateNextDelay(delay, options);
    await Assert.That(delay.TotalSeconds).IsEqualTo(4);

    delay = SubscriptionRetryHelper.CalculateNextDelay(delay, options);
    await Assert.That(delay.TotalSeconds).IsEqualTo(8);

    delay = SubscriptionRetryHelper.CalculateNextDelay(delay, options);
    await Assert.That(delay.TotalSeconds).IsEqualTo(16);

    delay = SubscriptionRetryHelper.CalculateNextDelay(delay, options);
    await Assert.That(delay.TotalSeconds).IsEqualTo(32);

    delay = SubscriptionRetryHelper.CalculateNextDelay(delay, options);
    await Assert.That(delay.TotalSeconds).IsEqualTo(64);

    delay = SubscriptionRetryHelper.CalculateNextDelay(delay, options);
    await Assert.That(delay.TotalSeconds).IsEqualTo(120); // Capped

    // Further attempts stay at max
    delay = SubscriptionRetryHelper.CalculateNextDelay(delay, options);
    await Assert.That(delay.TotalSeconds).IsEqualTo(120);
  }

  #endregion

  #region Retry Loop Tests

  [Test]
  public async Task SubscribeWithRetry_OnFirstSuccess_ReturnsImmediatelyAsync() {
    // Arrange
    var transport = new FailingTransport(failureCount: 0); // Succeeds immediately
    var options = _createResilienceOptions();
    var state = new SubscriptionState(new TransportDestination("test-topic"));

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport,
      state.Destination,
      async (_, _, _) => await Task.CompletedTask,
      state,
      options,
      NullLogger.Instance,
      CancellationToken.None
    );

    // Assert
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(state.AttemptCount).IsEqualTo(0); // No retries needed
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task SubscribeWithRetry_OnFailureThenSuccess_RetriesAndSucceedsAsync() {
    // Arrange
    var transport = new FailingTransport(failureCount: 3); // Fails 3 times, then succeeds
    var options = _createResilienceOptions();
    options.InitialRetryDelay = TimeSpan.FromMilliseconds(10); // Fast for tests
    var state = new SubscriptionState(new TransportDestination("test-topic"));

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport,
      state.Destination,
      async (_, _, _) => await Task.CompletedTask,
      state,
      options,
      NullLogger.Instance,
      CancellationToken.None
    );

    // Assert
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(4); // 3 failures + 1 success
  }

  [Test]
  public async Task SubscribeWithRetry_WithRetryIndefinitelyFalse_MarksAsFailedAfterInitialAttemptsAsync() {
    // Arrange
    var transport = new FailingTransport(failureCount: 100); // Always fails
    var options = _createResilienceOptions();
    options.InitialRetryAttempts = 3;
    options.RetryIndefinitely = false;
    options.InitialRetryDelay = TimeSpan.FromMilliseconds(10);
    var state = new SubscriptionState(new TransportDestination("test-topic"));

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport,
      state.Destination,
      async (_, _, _) => await Task.CompletedTask,
      state,
      options,
      NullLogger.Instance,
      CancellationToken.None
    );

    // Assert
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Failed);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(3); // Stops after InitialRetryAttempts
  }

  [Test]
  public async Task SubscribeWithRetry_WhenCancelled_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var transport = new FailingTransport(failureCount: 100); // Always fails
    var options = _createResilienceOptions();
    options.InitialRetryDelay = TimeSpan.FromMilliseconds(50);
    var state = new SubscriptionState(new TransportDestination("test-topic"));
    using var cts = new CancellationTokenSource();

    // Act - Cancel after a short delay
    _ = Task.Run(async () => {
      await Task.Delay(100);
      cts.Cancel();
    });

    // Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () => {
      await SubscriptionRetryHelper.SubscribeWithRetryAsync(
        transport,
        state.Destination,
        async (_, _, _) => await Task.CompletedTask,
        state,
        options,
        NullLogger.Instance,
        cts.Token
      );
    });
  }

  [Test]
  public async Task SubscribeWithRetry_SetsRecoveringStatus_DuringRetryAsync() {
    // Arrange
    var transport = new FailingTransport(failureCount: 2);
    var options = _createResilienceOptions();
    options.InitialRetryDelay = TimeSpan.FromMilliseconds(10);
    var state = new SubscriptionState(new TransportDestination("test-topic"));
    var statusDuringRetry = SubscriptionStatus.Pending;

    // Set up callback to capture status during retry (after first failure)
    transport.OnSubscribeAttempt = () => {
      if (transport.SubscribeCallCount == 2) { // Second attempt - after first failure, status should be Recovering
        statusDuringRetry = state.Status;
      }
    };

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport,
      state.Destination,
      async (_, _, _) => await Task.CompletedTask,
      state,
      options,
      NullLogger.Instance,
      CancellationToken.None
    );

    // Assert - status should have been Recovering during retries
    await Assert.That(statusDuringRetry).IsEqualTo(SubscriptionStatus.Recovering);
  }

  [Test]
  public async Task SubscribeWithRetry_TracksLastError_OnFailureAsync() {
    // Arrange
    var expectedException = new InvalidOperationException("Test failure");
    var transport = new FailingTransport(failureCount: 1, exceptionToThrow: expectedException);
    var options = _createResilienceOptions();
    options.InitialRetryDelay = TimeSpan.FromMilliseconds(10);
    var state = new SubscriptionState(new TransportDestination("test-topic"));

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport,
      state.Destination,
      async (_, _, _) => await Task.CompletedTask,
      state,
      options,
      NullLogger.Instance,
      CancellationToken.None
    );

    // Assert - after success, last error should still be tracked
    await Assert.That(state.LastError).IsNotNull();
    await Assert.That(state.LastError!.Message).IsEqualTo("Test failure");
    await Assert.That(state.LastErrorTime).IsNotNull();
  }

  #endregion

  #region Recovery Handler Tests

  [Test]
  public async Task Worker_WithRecoveryTransport_RegistersRecoveryHandlerAsync() {
    // Arrange
    var transport = new RecoveringFakeTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));
    var resilienceOptions = _createResilienceOptions();

    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton<IDispatcher>(new FakeDispatcher());
    serviceCollection.AddSingleton(resilienceOptions);
    var serviceProvider = serviceCollection.BuildServiceProvider();

    // Act - create worker (should register recovery handler)
    var worker = _createWorkerWithResilience(transport, options, resilienceOptions, serviceProvider);

    // Assert
    await Assert.That(transport.HasRecoveryHandler).IsTrue()
      .Because("Worker should register a recovery handler with ITransportWithRecovery");
  }

  [Test]
  public async Task Worker_OnRecovery_ResubscribesAllDestinationsAsync() {
    // Arrange
    var transport = new RecoveringFakeTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));
    options.Destinations.Add(new TransportDestination("topic2"));
    var resilienceOptions = _createResilienceOptions();

    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton<IDispatcher>(new FakeDispatcher());
    serviceCollection.AddSingleton(resilienceOptions);
    var serviceProvider = serviceCollection.BuildServiceProvider();

    var worker = _createWorkerWithResilience(transport, options, resilienceOptions, serviceProvider);

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(100); // Let subscriptions complete

    var initialSubscribeCount = transport.SubscribeCallCount;
    await Assert.That(initialSubscribeCount).IsEqualTo(2);

    // Act - simulate recovery
    await transport.SimulateRecoveryAsync();
    await Task.Delay(100); // Let re-subscription complete

    // Assert
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(4) // 2 initial + 2 recovery
      .Because("Worker should re-subscribe to all destinations on recovery");

    cts.Cancel();
  }

  #endregion

  #region Partial Subscription Tests

  [Test]
  public async Task Worker_WithPartialFailures_ContinuesWithSuccessfulSubscriptionsAsync() {
    // Arrange
    var transport = new SelectiveFailingTransport(failingTopics: ["failing-topic"]);
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("success-topic"));
    options.Destinations.Add(new TransportDestination("failing-topic"));
    var resilienceOptions = _createResilienceOptions();
    resilienceOptions.AllowPartialSubscriptions = true;
    resilienceOptions.InitialRetryAttempts = 2;
    resilienceOptions.RetryIndefinitely = false;
    resilienceOptions.InitialRetryDelay = TimeSpan.FromMilliseconds(10);

    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton<IDispatcher>(new FakeDispatcher());
    serviceCollection.AddSingleton(resilienceOptions);
    var serviceProvider = serviceCollection.BuildServiceProvider();

    var worker = _createWorkerWithResilience(transport, options, resilienceOptions, serviceProvider);

    using var cts = new CancellationTokenSource();

    // Act
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(200); // Give time for subscriptions

    // Assert - should have at least one successful subscription
    await Assert.That(transport.SuccessfulSubscriptions).Count().IsGreaterThanOrEqualTo(1)
      .Because("Worker should continue with successful subscriptions when AllowPartialSubscriptions=true");

    cts.Cancel();
  }

  #endregion

  #region Test Helpers

  private static SubscriptionResilienceOptions _createResilienceOptions() {
    return new SubscriptionResilienceOptions {
      InitialRetryAttempts = 5,
      InitialRetryDelay = TimeSpan.FromSeconds(1),
      MaxRetryDelay = TimeSpan.FromSeconds(120),
      BackoffMultiplier = 2.0,
      RetryIndefinitely = true,
      HealthCheckInterval = TimeSpan.FromMinutes(1),
      AllowPartialSubscriptions = true
    };
  }

  private static TransportConsumerWorker _createWorkerWithResilience(
    ITransport transport,
    TransportConsumerOptions options,
    SubscriptionResilienceOptions resilienceOptions,
    IServiceProvider serviceProvider
  ) {
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = new JsonSerializerOptions();
    var orderedProcessor = new OrderedStreamProcessor(parallelizeStreams: false, logger: null);

    return new TransportConsumerWorker(
      transport,
      options,
      resilienceOptions,
      scopeFactory,
      jsonOptions,
      orderedProcessor,
      lifecycleMessageDeserializer: null,
      lifecycleInvoker: null,
      tracer: null,
      NullLogger<TransportConsumerWorker>.Instance
    );
  }

  #endregion

  #region Test Doubles

  private sealed class FailingTransport : ITransport {
    private readonly int _failureCount;
    private readonly Exception _exceptionToThrow;
    private int _currentFailureCount;

    public int SubscribeCallCount { get; private set; }
    public Action? OnSubscribeAttempt { get; set; }

    public FailingTransport(int failureCount, Exception? exceptionToThrow = null) {
      _failureCount = failureCount;
      _exceptionToThrow = exceptionToThrow ?? new InvalidOperationException("Subscription failed");
    }

    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) {
      SubscribeCallCount++;
      OnSubscribeAttempt?.Invoke();

      if (_currentFailureCount < _failureCount) {
        _currentFailureCount++;
        throw _exceptionToThrow;
      }

      return Task.FromResult<ISubscription>(new FakeSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class RecoveringFakeTransport : ITransport, ITransportWithRecovery {
    private Func<CancellationToken, Task>? _recoveryHandler;

    public int SubscribeCallCount { get; private set; }
    public bool HasRecoveryHandler => _recoveryHandler != null;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) {
      _recoveryHandler = onRecovered;
    }

    public async Task SimulateRecoveryAsync() {
      if (_recoveryHandler != null) {
        await _recoveryHandler(CancellationToken.None);
      }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) {
      SubscribeCallCount++;
      return Task.FromResult<ISubscription>(new FakeSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class SelectiveFailingTransport : ITransport {
    private readonly HashSet<string> _failingTopics;
    private readonly List<TransportDestination> _successfulSubscriptions = [];

    public SelectiveFailingTransport(IEnumerable<string> failingTopics) {
      _failingTopics = new HashSet<string>(failingTopics);
    }

    public IReadOnlyList<TransportDestination> SuccessfulSubscriptions => _successfulSubscriptions;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) {
      if (_failingTopics.Contains(destination.Address)) {
        throw new InvalidOperationException($"Subscription to {destination.Address} failed");
      }

      _successfulSubscriptions.Add(destination);
      return Task.FromResult<ISubscription>(new FakeSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class FakeSubscription : ISubscription {
    public bool IsActive => true;

#pragma warning disable CS0067 // Event is required by interface but not used in test
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }
  }

  private sealed class FakeDispatcher : IDispatcher {
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull =>
      throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync(object message) =>
      throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) =>
      throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull =>
      throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) =>
      throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull =>
      throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) =>
      throw new NotImplementedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull =>
      throw new NotImplementedException();
    public ValueTask LocalInvokeAsync(object message) =>
      throw new NotImplementedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull =>
      throw new NotImplementedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) =>
      throw new NotImplementedException();
    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) =>
      throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, Whizbang.Core.Dispatch.DispatchOptions options) where TMessage : notnull =>
      throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) =>
      throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, Whizbang.Core.Dispatch.DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) =>
      throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, Whizbang.Core.Dispatch.DispatchOptions options) =>
      throw new NotImplementedException();
    public ValueTask LocalInvokeAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) =>
      throw new NotImplementedException();
    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, Whizbang.Core.Dispatch.DispatchOptions options) =>
      throw new NotImplementedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
      throw new NotImplementedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) =>
      throw new NotImplementedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) =>
      throw new NotImplementedException();
    public Task CascadeMessageAsync(IMessage message, Whizbang.Core.Dispatch.DispatchMode mode, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Whizbang.Core.Dispatch.DispatchMode mode, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  #endregion
}
