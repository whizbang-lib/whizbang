using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage-round-23 tests for <see cref="LifecycleInvocationHelper"/> targeting the inbox
/// receptor-error path in <c>_processInboxMessagesAsync</c> (error metric recording + rethrow).
/// </summary>
[Category("Messaging")]
[Category("Lifecycle")]
public class LifecycleInvocationHelperCoverageTests {

  // A receptor failure mid-inbox-drain must not be swallowed: swallowing it here would let the
  // caller treat the message as processed and move on, silently dropping whatever the receptor
  // was supposed to do while the metric that would have surfaced the failure goes unrecorded.
  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_InboxReceptorThrows_RecordsErrorMetricAndRethrowsAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleMetrics(new WhizbangMetrics(factory));
    using var metricHelper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    var invoker = new _ThrowingReceptorInvoker(new InvalidOperationException("receptor exploded"));
    var deserializer = new _PassthroughDeserializer();
    var outboxMessages = new List<OutboxMessage>();
    var inboxMessages = new List<InboxMessage> { _createTestInboxMessage() };

    var context = new DistributeLifecycleContext(
      outboxMessages, inboxMessages, _createScopeFactory(invoker), deserializer, null,
      EnableLifecycleTracing: true, Metrics: metrics);

    // Act & Assert - the inline stage awaits inbox processing synchronously, so the receptor's
    // exception must propagate to the caller rather than being swallowed.
    await Assert.That(async () => await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PostDistributeDetached,
        LifecycleStage.PostDistributeInline,
        context))
      .ThrowsExactly<InvalidOperationException>()
      .Because("the inline stage must not swallow a receptor failure while draining the inbox");

    // Assert - the error metric was recorded with real tag content (not just a short-circuited
    // null-conditional) before the exception was rethrown.
    var errorMeasurements = metricHelper.GetByName("whizbang.lifecycle.receptor.errors")
      .Where(m => m.Tags["stage"] == "PostDistributeInline")
      .ToList();
    await Assert.That(errorMeasurements.Count).IsGreaterThanOrEqualTo(1)
      .Because("the catch block records a receptor error for the inline stage before rethrowing");
    await Assert.That(errorMeasurements[0].Tags["message_type"]).IsEqualTo("TestMessage, TestAssembly")
      .Because("the recorded tag must reflect the actual inbox message type, not a placeholder");
    await Assert.That(errorMeasurements[0].Tags["error_type"]).IsEqualTo(nameof(InvalidOperationException))
      .Because("the recorded tag must reflect the actual exception type that was caught");
  }

  // ========================================
  // Test Helpers
  // ========================================

  private static IServiceScopeFactory _createScopeFactory(IReceptorInvoker invoker) {
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    var sp = services.BuildServiceProvider();
    return sp.GetRequiredService<IServiceScopeFactory>();
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
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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

  private sealed class _ThrowingReceptorInvoker(Exception toThrow) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      throw toThrow;
  }

  private sealed class _PassthroughDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) =>
      new _TestMessage { Value = "deserialized" };

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) =>
      new _TestMessage { Value = "deserialized" };

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) =>
      new _TestMessage { Value = "deserialized" };

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) =>
      new _TestMessage { Value = "deserialized" };
  }

  private sealed record _TestMessage {
    public required string Value { get; init; }
  }
}
