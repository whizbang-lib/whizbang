using System.Text.Json;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for LifecycleInvocationHelper - static helper for invoking lifecycle receptors.
/// Covers null-safe early returns, message processing, and error logging.
/// </summary>
[Category("Messaging")]
[Category("Lifecycle")]
public class LifecycleInvocationHelperTests {

  // ========================================
  // InvokeDistributeLifecycleStagesAsync - Null Safety Tests
  // ========================================

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithNullLifecycleInvoker_ReturnsEarlyAsync() {
    // Arrange
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage>();
    var deserializer = new FakeLifecycleMessageDeserializer();

    // Act - should not throw
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      null,
      deserializer,
      null);

    // Assert - deserializer should not be called
    await Assert.That(deserializer.CallCount).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithNullDeserializer_ReturnsEarlyAsync() {
    // Arrange
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage>();
    var invoker = new FakeLifecycleInvoker();

    // Act - should not throw
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      null,
      null);

    // Assert - invoker should not be called
    await Assert.That(invoker.Invocations.Count).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithBothNull_ReturnsEarlyAsync() {
    // Arrange
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage>();

    // Act - should not throw
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      null,
      null,
      null);

    // Assert - outboxMessages list is still the same object (early return worked)
    await Assert.That(outboxMessages.Count).IsEqualTo(0);
  }

  // ========================================
  // InvokeDistributeLifecycleStagesAsync - Message Processing Tests
  // ========================================

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithOutboxMessages_InvokesInlineStageAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage>();

    // Act
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Assert - inline stage should be invoked synchronously
    var inlineInvocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.PostDistributeInline).ToList();
    await Assert.That(inlineInvocations.Count).IsEqualTo(1);
    await Assert.That(inlineInvocations[0].Context!.MessageSource).IsEqualTo(MessageSource.Outbox);
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithInboxMessages_InvokesInlineStageAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage> { _createTestInboxMessage() };

    // Act
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Assert - inline stage should be invoked synchronously
    var inlineInvocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.PostDistributeInline).ToList();
    await Assert.That(inlineInvocations.Count).IsEqualTo(1);
    await Assert.That(inlineInvocations[0].Context!.MessageSource).IsEqualTo(MessageSource.Inbox);
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithBothMessageTypes_InvokesBothAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage> { _createTestInboxMessage() };

    // Act
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Assert - both outbox and inbox should be processed for inline stage
    var inlineInvocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.PostDistributeInline).ToList();
    await Assert.That(inlineInvocations.Count).IsEqualTo(2);
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithEmptyLists_DoesNotInvokeAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage>();

    // Act
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Assert - no invocations for empty lists
    await Assert.That(invoker.Invocations.Count).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_SetsCorrectContextForOutboxAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage>();

    // Act
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Assert - context should have correct stage and source
    var invocation = invoker.Invocations.First(i => i.Stage == LifecycleStage.PostDistributeInline);
    await Assert.That(invocation.Context).IsNotNull();
    await Assert.That(invocation.Context!.CurrentStage).IsEqualTo(LifecycleStage.PostDistributeInline);
    await Assert.That(invocation.Context!.MessageSource).IsEqualTo(MessageSource.Outbox);
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_SetsCorrectContextForInboxAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage> { _createTestInboxMessage() };

    // Act
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Assert - context should have correct stage and source
    var invocation = invoker.Invocations.First(i => i.Stage == LifecycleStage.PostDistributeInline);
    await Assert.That(invocation.Context).IsNotNull();
    await Assert.That(invocation.Context!.CurrentStage).IsEqualTo(LifecycleStage.PostDistributeInline);
    await Assert.That(invocation.Context!.MessageSource).IsEqualTo(MessageSource.Inbox);
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_InvokesAsyncStageInBackgroundAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage>();

    // Act
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Wait briefly for background task to complete
    await Task.Delay(100);

    // Assert - async stage should be invoked in background
    var asyncInvocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.PostDistributeAsync).ToList();
    await Assert.That(asyncInvocations.Count).IsEqualTo(1);
  }

  // ========================================
  // InvokeAsyncOnlyLifecycleStage - Null Safety Tests
  // ========================================

  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithNullLifecycleInvoker_ReturnsEarlyAsync() {
    // Arrange
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage>();
    var deserializer = new FakeLifecycleMessageDeserializer();

    // Act - should not throw
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      outboxMessages,
      inboxMessages,
      null,
      deserializer,
      null);

    await Task.Delay(50);

    // Assert - deserializer should not be called
    await Assert.That(deserializer.CallCount).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithNullDeserializer_ReturnsEarlyAsync() {
    // Arrange
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage>();
    var invoker = new FakeLifecycleInvoker();

    // Act - should not throw
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      outboxMessages,
      inboxMessages,
      invoker,
      null,
      null);

    await Task.Delay(50);

    // Assert - invoker should not be called
    await Assert.That(invoker.Invocations.Count).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithBothNull_ReturnsEarlyAsync() {
    // Arrange
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage>();

    // Act - should not throw
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      outboxMessages,
      inboxMessages,
      null,
      null,
      null);

    // Assert - outboxMessages list is still the same object (early return worked)
    await Assert.That(outboxMessages.Count).IsEqualTo(0);
  }

  // ========================================
  // InvokeAsyncOnlyLifecycleStage - Message Processing Tests
  // ========================================

  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithOutboxMessages_InvokesInBackgroundAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage>();

    // Act
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Wait for background task
    await Task.Delay(100);

    // Assert - async stage should be invoked
    var invocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.DistributeAsync).ToList();
    await Assert.That(invocations.Count).IsEqualTo(1);
    await Assert.That(invocations[0].Context!.MessageSource).IsEqualTo(MessageSource.Outbox);
  }

  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithInboxMessages_InvokesInBackgroundAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage> { _createTestInboxMessage() };

    // Act
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Wait for background task
    await Task.Delay(100);

    // Assert - async stage should be invoked
    var invocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.DistributeAsync).ToList();
    await Assert.That(invocations.Count).IsEqualTo(1);
    await Assert.That(invocations[0].Context!.MessageSource).IsEqualTo(MessageSource.Inbox);
  }

  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithBothMessageTypes_InvokesBothInBackgroundAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage> { _createTestInboxMessage() };

    // Act
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Wait for background task
    await Task.Delay(100);

    // Assert - both should be processed
    await Assert.That(invoker.Invocations.Count).IsEqualTo(2);
  }

  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithEmptyLists_DoesNotInvokeAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage>();

    // Act
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Wait for any potential background task
    await Task.Delay(50);

    // Assert - no invocations for empty lists
    await Assert.That(invoker.Invocations.Count).IsEqualTo(0);
  }

  // ========================================
  // Error Handling Tests
  // ========================================

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithAsyncStageError_LogsErrorAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker { ThrowOnAsyncStage = true };
    var deserializer = new FakeLifecycleMessageDeserializer();
    var logger = new FakeLogger();
    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage>();

    // Act - inline stage succeeds but async stage errors
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      logger);

    // Wait for background task to complete (and fail)
    await Task.Delay(100);

    // Assert - error should be logged
    await Assert.That(logger.ErrorCount).IsGreaterThan(0);
  }

  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithError_LogsErrorAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker { ThrowOnAsyncStage = true };
    var deserializer = new FakeLifecycleMessageDeserializer();
    var logger = new FakeLogger();
    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage>();

    // Act
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      logger);

    // Wait for background task to complete (and fail)
    await Task.Delay(100);

    // Assert - error should be logged
    await Assert.That(logger.ErrorCount).IsGreaterThan(0);
  }

  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithNullLogger_DoesNotThrowOnErrorAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker { ThrowOnAsyncStage = true };
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage>();

    // Act - should not throw even with null logger
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Wait for background task
    await Task.Delay(100);

    // Assert - outboxMessages is still empty (no exception thrown)
    await Assert.That(outboxMessages.Count).IsEqualTo(1);
  }

  // ========================================
  // Multiple Messages Tests
  // ========================================

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithMultipleOutboxMessages_InvokesAllAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage> {
      _createTestOutboxMessage(),
      _createTestOutboxMessage(),
      _createTestOutboxMessage()
    };
    var inboxMessages = new List<InboxMessage>();

    // Act
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Wait for background task
    await Task.Delay(100);

    // Assert - inline should have 3, async should have 3
    var inlineInvocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.PostDistributeInline).ToList();
    var asyncInvocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.PostDistributeAsync).ToList();
    await Assert.That(inlineInvocations.Count).IsEqualTo(3);
    await Assert.That(asyncInvocations.Count).IsEqualTo(3);
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_WithMultipleInboxMessages_InvokesAllAsync() {
    // Arrange
    var invoker = new FakeLifecycleInvoker();
    var deserializer = new FakeLifecycleMessageDeserializer();
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage> {
      _createTestInboxMessage(),
      _createTestInboxMessage()
    };

    // Act
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      outboxMessages,
      inboxMessages,
      invoker,
      deserializer,
      null);

    // Wait for background task
    await Task.Delay(100);

    // Assert - inline should have 2, async should have 2
    var inlineInvocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.PostDistributeInline).ToList();
    var asyncInvocations = invoker.Invocations.Where(i => i.Stage == LifecycleStage.PostDistributeAsync).ToList();
    await Assert.That(inlineInvocations.Count).IsEqualTo(2);
    await Assert.That(asyncInvocations.Count).IsEqualTo(2);
  }

  // ========================================
  // Test Helpers
  // ========================================

  private static OutboxMessage _createTestOutboxMessage() {
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{\"value\":\"test\"}").RootElement,
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow
      }]
    };

    return new OutboxMessage {
      MessageId = messageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = envelope.Hops },
      EnvelopeType = "MessageEnvelope`1[[TestMessage, TestAssembly]]",
      MessageType = "TestMessage, TestAssembly"
    };
  }

  private static InboxMessage _createTestInboxMessage() {
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{\"value\":\"test\"}").RootElement,
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow
      }]
    };

    return new InboxMessage {
      MessageId = messageId.Value,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = "MessageEnvelope`1[[TestMessage, TestAssembly]]",
      MessageType = "TestMessage, TestAssembly"
    };
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeLifecycleInvoker : ILifecycleInvoker {
    private readonly List<LifecycleInvocation> _invocations = [];
    private readonly object _lock = new();

    public bool ThrowOnAsyncStage { get; set; }

    public List<LifecycleInvocation> Invocations {
      get {
        lock (_lock) {
          return _invocations.ToList();
        }
      }
    }

    public ValueTask InvokeAsync(object message, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      if (ThrowOnAsyncStage && stage.ToString().EndsWith("Async", StringComparison.Ordinal)) {
        throw new InvalidOperationException("Test exception in async stage");
      }

      lock (_lock) {
        _invocations.Add(new LifecycleInvocation {
          Message = message,
          Stage = stage,
          Context = context as LifecycleExecutionContext
        });
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed record LifecycleInvocation {
    public required object Message { get; init; }
    public required LifecycleStage Stage { get; init; }
    public LifecycleExecutionContext? Context { get; init; }
  }

  private sealed class FakeLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    private int _callCount;
    private readonly object _lock = new();

    public int CallCount {
      get {
        lock (_lock) { return _callCount; }
      }
    }

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) {
      lock (_lock) { _callCount++; }
      return new TestMessage { Value = "deserialized" };
    }

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) {
      lock (_lock) { _callCount++; }
      return new TestMessage { Value = "deserialized" };
    }

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) {
      lock (_lock) { _callCount++; }
      return new TestMessage { Value = "deserialized" };
    }

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
      lock (_lock) { _callCount++; }
      return new TestMessage { Value = "deserialized" };
    }
  }

  private sealed record TestMessage {
    public required string Value { get; init; }
  }

  private sealed class FakeLogger : ILogger {
    private int _errorCount;
    private readonly object _lock = new();

    public int ErrorCount {
      get {
        lock (_lock) { return _errorCount; }
      }
    }

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Error) {
        lock (_lock) { _errorCount++; }
      }
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
  }
}
