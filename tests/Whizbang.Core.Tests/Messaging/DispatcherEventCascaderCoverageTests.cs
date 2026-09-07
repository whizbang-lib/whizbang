using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage-round-23 tests for <see cref="DispatcherEventCascader"/> targeting the
/// non-message-value logging callback (<c>_onNonMessageValue</c>).
/// </summary>
[Category("Core")]
[Category("Messaging")]
public class DispatcherEventCascaderCoverageTests {

  // If a receptor returns something the extractor can't recognize as a message, that value is
  // silently dropped from the cascade -- with no log line here, a developer has no way to know
  // their receptor's return value was ignored, so the follow-on workflow it should have
  // triggered just never happens, with zero diagnostic trail.
  [Test]
  public async Task CascadeFromResultAsync_NonMessageResult_LogsNonMessageReturnTypeAsync() {
    // Arrange
    var services = new ServiceCollection();
    var dispatcher = new _NoOpDispatcher();
    services.AddSingleton<IDispatcher>(dispatcher);
    var provider = services.BuildServiceProvider();
    var logger = new _CapturingLogger();
    var cascader = new DispatcherEventCascader(provider, logger);

    // Act - a plain string is not IMessage, IRouted, a typed message enumerable, or ITuple, and
    // strings are explicitly excluded from the general-enumerable branch in MessageExtractor, so
    // it falls through to the non-message callback under test.
    await cascader.CascadeFromResultAsync("not-a-message", null);

    // Assert - the logger actually received the formatted content (not just a non-null receiver
    // going green on a short-circuited null-conditional).
    await Assert.That(logger.Messages.Count).IsEqualTo(1)
      .Because("exactly one unrecognized return value was produced by the receptor");
    await Assert.That(logger.Messages[0]).Contains("System.String")
      .Because("the log message must name the actual offending type so a developer can find the receptor at fault");
    await Assert.That(logger.Levels[0]).IsEqualTo(LogLevel.Error);
    await Assert.That(dispatcher.CascadedMessages.Count).IsEqualTo(0)
      .Because("a non-message value must never be forwarded to the dispatcher as if it were a message");
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class _CapturingLogger : ILogger<DispatcherEventCascader> {
    public List<string> Messages { get; } = [];
    public List<LogLevel> Levels { get; } = [];

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Levels.Add(logLevel);
      Messages.Add(formatter(state, exception));
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
  }

  private sealed class _NoOpDispatcher : IDispatcher {
    public List<IMessage> CascadedMessages { get; } = [];
    public IMessageEnvelope? LastSourceEnvelope { get; private set; }

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull =>
      Task.FromResult<IDeliveryReceipt>(new _FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync(object message) =>
      Task.FromResult<IDeliveryReceipt>(new _FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) =>
      Task.FromResult<IDeliveryReceipt>(new _FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull =>
      Task.FromResult<IDeliveryReceipt>(new _FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) =>
      Task.FromResult<IDeliveryReceipt>(new _FakeDeliveryReceipt());

    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) =>
      Task.FromResult<IDeliveryReceipt>(new _FakeDeliveryReceipt());

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

    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) =>
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

  private sealed class _FakeDeliveryReceipt : IDeliveryReceipt {
    public MessageId MessageId => MessageId.New();
    public CorrelationId? CorrelationId => null;
    public MessageId? CausationId => null;
    public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
    public string Destination => "test-destination";
    public DeliveryStatus Status => DeliveryStatus.Delivered;
    public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
    public Guid? StreamId => null;
  }
}
