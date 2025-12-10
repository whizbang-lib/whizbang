using System.Text.Json;
using System.Text.Json.Serialization;
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
    // Register test context with global registry for this test
    Whizbang.Core.Serialization.JsonContextRegistry.RegisterContext(TestWorkerJsonContext.Default);

    var invokedWhileScopeActive = false;
    var perspectiveInvokerCalled = false;

    // Create test event
    var testEvent = new ServiceBusWorkerTestEvent { Data = "test data" };
    var envelope = CreateTestEnvelope(testEvent);

    // Create service collection with test invoker
    var services = new ServiceCollection();

    // Register scoped marker service to verify scope is active
    services.AddScoped<ScopeMarker>();

    // Register test work coordinator strategy that returns the message for processing
    services.AddScoped<IWorkCoordinatorStrategy>(sp => new TestWorkCoordinatorStrategy(() => {
      var inboxWork = new List<InboxWork> {
        new InboxWork<ServiceBusWorkerTestEvent> {
          MessageId = envelope.MessageId.Value,
          Envelope = envelope,
          StreamId = Guid.NewGuid(),
          PartitionNumber = 1,
          Status = MessageProcessingStatus.Stored,
          Flags = WorkBatchFlags.None,
          SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }
      };
      return new WorkBatch { InboxWork = inboxWork, OutboxWork = new List<OutboxWork>() };
    }));

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
    var instanceProvider = new TestServiceInstanceProvider();
    var transport = new TestTransport();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var logger = new TestLogger<ServiceBusConsumerWorker>();

    // We can't easily test the protected ExecuteAsync method directly,
    // so we'll test the underlying HandleMessageAsync behavior through
    // a reflection call (not ideal, but necessary for regression test)
    var orderedProcessor = new OrderedStreamProcessor();
    var worker = new ServiceBusConsumerWorker(
      instanceProvider,
      transport,
      scopeFactory,
      jsonOptions,
      logger,
      orderedProcessor,
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
    // Register test context with global registry for this test
    Whizbang.Core.Serialization.JsonContextRegistry.RegisterContext(TestWorkerJsonContext.Default);

    var perspectiveInvokerCalled = false;

    var testEvent = new ServiceBusWorkerTestEvent { Data = "test data" };
    var envelope = CreateTestEnvelope(testEvent);

    // Register test work coordinator strategy that returns EMPTY work (duplicate message)
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(sp => new TestWorkCoordinatorStrategy(() => {
      // Return empty work batch - this simulates the message being a duplicate
      // (atomic INSERT ... ON CONFLICT DO NOTHING at database level)
      return new WorkBatch {
        InboxWork = new List<InboxWork>(),  // Empty - message was duplicate
        OutboxWork = new List<OutboxWork>()
      };
    }));

    services.AddScoped<IPerspectiveInvoker>(sp => new TestPerspectiveInvoker(() => {
      perspectiveInvokerCalled = true;
    }));

    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var instanceProvider = new TestServiceInstanceProvider();
    var transport = new TestTransport();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var logger = new TestLogger<ServiceBusConsumerWorker>();
    var orderedProcessor = new OrderedStreamProcessor();

    var worker = new ServiceBusConsumerWorker(
      instanceProvider,
      transport,
      scopeFactory,
      jsonOptions,
      logger,
      orderedProcessor,
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
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
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
  public string Data { get; init; } = string.Empty;
}

/// <summary>
/// JsonSerializerContext for test event types
/// </summary>
[JsonSerializable(typeof(ServiceBusWorkerTestEvent))]
[JsonSerializable(typeof(MessageEnvelope<ServiceBusWorkerTestEvent>))]
[JsonSerializable(typeof(EnvelopeMetadata))]
internal partial class TestWorkerJsonContext : JsonSerializerContext {
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
  private bool _isInitialized;

  public bool IsInitialized => _isInitialized;
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    _isInitialized = true;
    return Task.CompletedTask;
  }

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
/// Test double for IWorkCoordinatorStrategy
/// </summary>
internal class TestWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
  private readonly Func<WorkBatch> _flushFunc;

  public TestWorkCoordinatorStrategy(Func<WorkBatch> flushFunc) {
    _flushFunc = flushFunc;
  }

  public void QueueOutboxMessage(OutboxMessage message) { }
  public void QueueInboxMessage(InboxMessage message) { }
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

  public Task<WorkBatch> FlushAsync(WorkBatchFlags flags, CancellationToken ct = default) {
    return Task.FromResult(_flushFunc());
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

/// <summary>
/// Test double for IServiceInstanceProvider
/// </summary>
internal class TestServiceInstanceProvider : IServiceInstanceProvider {
  private readonly Guid _instanceId = Guid.NewGuid();

  public Guid InstanceId => _instanceId;
  public string ServiceName => "test-service";
  public string HostName => "test-host";
  public int ProcessId => 12345;

  public ServiceInstanceInfo ToInfo() {
    return new ServiceInstanceInfo {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
