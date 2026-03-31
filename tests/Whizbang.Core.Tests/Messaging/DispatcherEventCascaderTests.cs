using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for DispatcherEventCascader.
/// </summary>
/// <tests>DispatcherEventCascader</tests>
[Category("Core")]
[Category("Messaging")]
public class DispatcherEventCascaderTests {
  // === Constructor Tests ===

  [Test]
  public async Task DispatcherEventCascader_Constructor_NullServiceProvider_ThrowsAsync() {
    // Act & Assert
    await Assert.That(() => new DispatcherEventCascader(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task DispatcherEventCascader_Constructor_ValidServiceProvider_SucceedsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var provider = services.BuildServiceProvider();

    // Act
    var cascader = new DispatcherEventCascader(provider);

    // Assert
    await Assert.That(cascader).IsNotNull();
  }

  // === CascadeFromResultAsync Tests ===

  [Test]
  public async Task CascadeFromResultAsync_NullResult_ThrowsAsync() {
    // Arrange
    var (cascader, _) = _createCascader();

    // Act & Assert
    await Assert.That(() => cascader.CascadeFromResultAsync(null!, null))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task CascadeFromResultAsync_SingleEvent_DispatchesToDispatcherAsync() {
    // Arrange
    var (cascader, dispatcher) = _createCascader();
    var testEvent = new TestCascadeEvent { Id = "test-123" };

    // Act
    await cascader.CascadeFromResultAsync(testEvent, null);

    // Assert
    await Assert.That(dispatcher.CascadedMessages.Count).IsEqualTo(1);
  }

  [Test]
  public async Task CascadeFromResultAsync_EventArray_DispatchesAllEventsAsync() {
    // Arrange
    var (cascader, dispatcher) = _createCascader();
    var events = new IMessage[] {
      new TestCascadeEvent { Id = "event-1" },
      new TestCascadeEvent { Id = "event-2" },
      new TestCascadeEvent { Id = "event-3" }
    };

    // Act
    await cascader.CascadeFromResultAsync(events, null);

    // Assert
    await Assert.That(dispatcher.CascadedMessages.Count).IsEqualTo(3);
  }

  [Test]
  public async Task CascadeFromResultAsync_PropagatesSourceEnvelopeAsync() {
    // Arrange
    var (cascader, dispatcher) = _createCascader();
    var testEvent = new TestCascadeEvent { Id = "test-123" };
    var sourceEnvelope = new FakeMessageEnvelope(MessageId.New(), null);

    // Act
    await cascader.CascadeFromResultAsync(testEvent, sourceEnvelope);

    // Assert
    await Assert.That(dispatcher.LastSourceEnvelope).IsEqualTo(sourceEnvelope);
  }

  [Test]
  public async Task CascadeFromResultAsync_CancellationRequested_ThrowsAsync() {
    // Arrange
    var (cascader, _) = _createCascader();
    var testEvent = new TestCascadeEvent { Id = "test-123" };
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(() => cascader.CascadeFromResultAsync(testEvent, null, cancellationToken: cts.Token))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task CascadeFromResultAsync_LazilyResolvesDispatcherAsync() {
    // Arrange - create cascader with dispatcher NOT yet in DI
    var services = new ServiceCollection();
    var dispatcher = new TestDispatcher();

    // Register dispatcher after cascader is created
    services.AddSingleton<IDispatcher>(dispatcher);
    var provider = services.BuildServiceProvider();
    var cascader = new DispatcherEventCascader(provider);

    var testEvent = new TestCascadeEvent { Id = "test-123" };

    // Act - dispatcher should be lazily resolved
    await cascader.CascadeFromResultAsync(testEvent, null);

    // Assert
    await Assert.That(dispatcher.CascadedMessages.Count).IsEqualTo(1);
  }

  // === Helper Methods ===

  private static (DispatcherEventCascader cascader, TestDispatcher dispatcher) _createCascader() {
    var services = new ServiceCollection();
    var dispatcher = new TestDispatcher();
    services.AddSingleton<IDispatcher>(dispatcher);

    var provider = services.BuildServiceProvider();
    var cascader = new DispatcherEventCascader(provider);

    return (cascader, dispatcher);
  }

  // === Test Types ===

  private sealed class TestDispatcher : IDispatcher {
    public List<IMessage> CascadedMessages { get; } = [];
    public IMessageEnvelope? LastSourceEnvelope { get; private set; }

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull =>
      Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync(object message) =>
      Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) =>
      Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull =>
      Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) =>
      Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) =>
      Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());

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

    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) =>
      throw new NotImplementedException();

    public ValueTask LocalInvokeAsync(object message, DispatchOptions options) =>
      throw new NotImplementedException();

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) =>
      throw new NotImplementedException();

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) =>
      throw new NotImplementedException();

    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
      throw new NotImplementedException();

    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) =>
      throw new NotImplementedException();

    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) =>
      throw new NotImplementedException();

    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
      throw new NotImplementedException();

    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) =>
      throw new NotImplementedException();

    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull =>
      throw new NotImplementedException();

    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) =>
      throw new NotImplementedException();

    public Task CascadeMessageAsync(IMessage message, DispatchModes mode, CancellationToken cancellationToken = default) {
      CascadedMessages.Add(message);
      return Task.CompletedTask;
    }

    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) {
      CascadedMessages.Add(message);
      LastSourceEnvelope = sourceEnvelope;
      return Task.CompletedTask;
    }

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotImplementedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotImplementedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotImplementedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => throw new NotImplementedException();
  }

  private sealed class FakeDeliveryReceipt : IDeliveryReceipt {
    public MessageId MessageId => MessageId.New();
    public CorrelationId? CorrelationId => null;
    public MessageId? CausationId => null;
    public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
    public string Destination => "test-destination";
    public DeliveryStatus Status => DeliveryStatus.Delivered;
    public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
    public Guid? StreamId => null;
  }

  private sealed class FakeMessageEnvelope : IMessageEnvelope {
    private readonly List<MessageHop> _hops = [];

    public FakeMessageEnvelope(MessageId messageId, CorrelationId? correlationId) {
      MessageId = messageId;
      _hops.Add(new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "test-service",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 1234
        },
        CorrelationId = correlationId
      });
    }

    public int Version => 1;
    public MessageDispatchContext DispatchContext { get; } = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
    public MessageId MessageId { get; }
    public object Payload => new { };
    public List<MessageHop> Hops => _hops;

    public void AddHop(MessageHop hop) => _hops.Add(hop);
    public DateTimeOffset GetMessageTimestamp() => _hops[0].Timestamp;
    public CorrelationId? GetCorrelationId() => _hops[0].CorrelationId;
    public MessageId? GetCausationId() => _hops[0].CausationId;
    public JsonElement? GetMetadata(string key) => null;
    public SecurityContext? GetCurrentSecurityContext() => null;
    public ScopeContext? GetCurrentScope() => null;
  }

  public sealed class TestCascadeEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; } = Guid.NewGuid();
    public required string Id { get; init; }
  }
}
