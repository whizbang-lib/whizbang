using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Regression tests for ServiceBusConsumerWorker.
/// Ensures critical scope disposal ordering is maintained.
/// </summary>
public class ServiceBusConsumerWorkerTests {
  /// <summary>
  /// REGRESSION TEST: Verifies that perspectives are invoked BEFORE the scope is disposed.
  ///
  /// **Background**: In a previous bug, InvokePerspectivesAsync() was called AFTER scope.DisposeAsync(),
  /// causing scoped services to be disposed before perspectives could use them. This resulted in
  /// ObjectDisposedException when perspectives tried to access DbContext, IEventStore, etc.
  ///
  /// **This test ensures**:
  /// 1. IPerspectiveInvoker.InvokePerspectivesAsync() is called
  /// 2. It's called while the scope is still active (not disposed)
  /// 3. Scoped services are available when perspectives need them
  ///
  /// **If this test fails**, it means someone moved InvokePerspectivesAsync() to execute after
  /// scope disposal, which will break perspective materialization in production.
  /// </summary>
  [Test]
  public async Task HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync() {
    // Arrange
    var invokedWhileScopeActive = false;
    var perspectiveInvokerCalled = false;

    // Create test event
    var testEvent = new ServiceBusWorkerTestEvent { Data = "test data" };
    var envelope = CreateTestEnvelope(testEvent);

    // Create service collection with test invoker
    var services = new ServiceCollection();

    // Register scoped marker service to verify scope is active
    services.AddScoped<ScopeMarker>();

    // Register test inbox (resolved from scope in HandleMessageAsync)
    services.AddScoped<IInbox, TestInbox>();

    // Register test perspective invoker that verifies scope is active
    services.AddScoped<IPerspectiveInvoker>(sp => {
      var scopeMarker = sp.GetService<ScopeMarker>();
      return new TestPerspectiveInvoker(() => {
        perspectiveInvokerCalled = true;

        // CRITICAL: Verify scope is still active when invoker runs
        // If scope was disposed, GetService<ScopeMarker>() would throw
        invokedWhileScopeActive = scopeMarker != null && !scopeMarker.IsDisposed;
      });
    });

    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    // Create test dependencies
    var transport = new TestTransport();
    var jsonOptions = new JsonSerializerOptions();
    var logger = new TestLogger<ServiceBusConsumerWorker>();

    // We can't easily test the protected ExecuteAsync method directly,
    // so we'll test the underlying HandleMessageAsync behavior through
    // a reflection call (not ideal, but necessary for regression test)
    var worker = new ServiceBusConsumerWorker(
      transport,
      scopeFactory,
      jsonOptions,
      logger,
      options: null
    );

    // Use reflection to access private HandleMessageAsync method
    var handleMessageMethod = typeof(ServiceBusConsumerWorker)
      .GetMethod("HandleMessageAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    // Act
    await (Task)handleMessageMethod!.Invoke(worker, [envelope, CancellationToken.None])!;

    // Assert
    await Assert.That(perspectiveInvokerCalled).IsTrue()
      .Because("IPerspectiveInvoker.InvokePerspectivesAsync() should have been called");

    await Assert.That(invokedWhileScopeActive).IsTrue()
      .Because("Perspectives must be invoked BEFORE scope disposal - scoped services must be available");
  }

  /// <summary>
  /// Test to verify that messages already processed (in inbox) are skipped.
  /// This prevents duplicate processing even if Service Bus redelivers.
  /// </summary>
  [Test]
  public async Task HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync() {
    // Arrange
    var perspectiveInvokerCalled = false;

    var testEvent = new ServiceBusWorkerTestEvent { Data = "test data" };
    var envelope = CreateTestEnvelope(testEvent);

    // Create test inbox that's already processed the message
    var testInbox = new TestInbox();
    await testInbox.MarkProcessedAsync(envelope.MessageId, CancellationToken.None);

    var services = new ServiceCollection();
    services.AddScoped<IInbox>(sp => testInbox);
    services.AddScoped<IPerspectiveInvoker>(sp => new TestPerspectiveInvoker(() => {
      perspectiveInvokerCalled = true;
    }));

    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var transport = new TestTransport();
    var jsonOptions = new JsonSerializerOptions();
    var logger = new TestLogger<ServiceBusConsumerWorker>();

    var worker = new ServiceBusConsumerWorker(
      transport,
      scopeFactory,
      jsonOptions,
      logger,
      options: null
    );

    var handleMessageMethod = typeof(ServiceBusConsumerWorker)
      .GetMethod("HandleMessageAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    // Act
    await (Task)handleMessageMethod!.Invoke(worker, [envelope, CancellationToken.None])!;

    // Assert
    await Assert.That(perspectiveInvokerCalled).IsFalse()
      .Because("Messages already in inbox should be skipped to prevent duplicate processing");
  }

  private static MessageEnvelope<ServiceBusWorkerTestEvent> CreateTestEnvelope(ServiceBusWorkerTestEvent payload) {
    // Create hop without PayloadType metadata - not needed for scope disposal test
    // In production, PayloadType would be a JsonElement from Service Bus deserialization
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceName = "TestService",
      Topic = "test-topic",
      Timestamp = DateTimeOffset.UtcNow
    };

    var envelope = new MessageEnvelope<ServiceBusWorkerTestEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [hop]
    };

    return envelope;
  }
}

/// <summary>
/// Test event for ServiceBusConsumerWorker tests (Worker-specific to avoid naming conflicts)
/// </summary>
public record ServiceBusWorkerTestEvent : IEvent {
  public required string Data { get; init; }
}

/// <summary>
/// Marker class to verify scope is active during perspective invocation
/// </summary>
public class ScopeMarker : IDisposable {
  public bool IsDisposed { get; private set; }

  public void Dispose() {
    IsDisposed = true;
    GC.SuppressFinalize(this);
  }
}

/// <summary>
/// Test double for IPerspectiveInvoker that tracks invocation
/// </summary>
internal class TestPerspectiveInvoker(Action onInvoke) : IPerspectiveInvoker {
  private readonly Action _onInvoke = onInvoke;

  public void QueueEvent(IEvent @event) {
    // No-op for testing
  }

  public async Task InvokePerspectivesAsync(CancellationToken cancellationToken = default) {
    _onInvoke();
    await Task.CompletedTask;
  }

  public ValueTask DisposeAsync() {
    GC.SuppressFinalize(this);
    return ValueTask.CompletedTask;
  }
}

/// <summary>
/// Test double for ITransport
/// </summary>
internal class TestTransport : ITransport {
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default) {
    return Task.FromResult<ISubscription>(new TestSubscription());
  }

  public Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }

  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope envelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default)
    where TRequest : notnull
    where TResponse : notnull {
    throw new NotImplementedException("Request-response not needed for worker tests");
  }

  public void Dispose() {
    GC.SuppressFinalize(this);
  }
}

/// <summary>
/// Test double for ISubscription
/// </summary>
internal class TestSubscription : ISubscription {
  public bool IsActive { get; private set; } = true;

  public Task PauseAsync() {
    IsActive = false;
    return Task.CompletedTask;
  }

  public Task ResumeAsync() {
    IsActive = true;
    return Task.CompletedTask;
  }

  public void Dispose() {
    IsActive = false;
    GC.SuppressFinalize(this);
  }
}

/// <summary>
/// Test double for IInbox
/// </summary>
internal class TestInbox : IInbox {
  private readonly HashSet<MessageId> _processed = [];

  public Task<bool> HasProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    return Task.FromResult(_processed.Contains(messageId));
  }

  public Task MarkProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    _processed.Add(messageId);
    return Task.CompletedTask;
  }

  public Task StoreAsync<TMessage>(MessageEnvelope<TMessage> envelope, string handlerName, CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }

  public Task<IReadOnlyList<InboxMessage>> GetPendingAsync(int maxMessages = 100, CancellationToken cancellationToken = default) {
    return Task.FromResult<IReadOnlyList<InboxMessage>>([]);
  }

  public Task CleanupExpiredAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }
}

/// <summary>
/// Test double for ILogger
/// </summary>
internal class TestLogger<T> : ILogger<T> {
  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(
    LogLevel logLevel,
    EventId eventId,
    TState state,
    Exception? exception,
    Func<TState, Exception?, string> formatter) {
    // No-op for testing
  }
}
