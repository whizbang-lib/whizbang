using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for ServiceBusConsumerWorker.
/// </summary>
public static class ServiceBusConsumerWorkerTests {
  // NOTE: Previous tests for inline perspective invocation have been removed.
  // The architecture changed - perspectives are now processed asynchronously via PerspectiveWorker
  // using checkpoints created by process_work_batch, not inline during message handling.
  // See ServiceBusConsumerWorker.cs:134-137 for architecture details.

  private static MessageEnvelope<ServiceBusWorkerTestEvent> _createTestEnvelope(ServiceBusWorkerTestEvent payload) {
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
  [StreamKey]
  public string Data { get; init; } = string.Empty;
}

/// <summary>
/// JsonSerializerContext for test event types
/// </summary>
[JsonSerializable(typeof(ServiceBusWorkerTestEvent))]
[JsonSerializable(typeof(MessageEnvelope<ServiceBusWorkerTestEvent>))]
[JsonSerializable(typeof(EnvelopeMetadata))]
internal sealed partial class TestWorkerJsonContext : JsonSerializerContext {
}

/// <summary>
/// Test double for ITransport
/// </summary>
internal sealed class TestTransport : ITransport {
  private bool _isInitialized;

  public bool IsInitialized => _isInitialized;
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    _isInitialized = true;
    return Task.CompletedTask;
  }

  public Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default) {
    return Task.FromResult<ISubscription>(new TestSubscription());
  }

  public Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType = null,
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
    // No unmanaged resources to dispose
  }
}

/// <summary>
/// Test double for ISubscription
/// </summary>
internal sealed class TestSubscription : ISubscription {
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
internal sealed class TestWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
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
internal sealed class TestLogger<T> : ILogger<T> {
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
internal sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
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
