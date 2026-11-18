// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// Branch coverage for <see cref="LifecycleInvocationHelper"/>'s receptor instrumentation. The
/// outbox path had only ever run without metrics, so its invocation, duration and error recordings
/// never executed; the inbox error path had only ever run with metrics, so a failure with no
/// metrics wired had never been shown to surface the receptor's own exception.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/LifecycleInvocationHelper.cs</code-under-test>
[Category("Messaging")]
[Category("Lifecycle")]
public class LifecycleInvocationHelperBranchCoverageTests {
  private const string INLINE_STAGE = "PostDistributeInline";
  private const string MESSAGE_TYPE = "TestMessage, TestAssembly";

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_OutboxReceptorSucceeds_WithMetrics_RecordsInvocationAndDurationAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleMetrics(new WhizbangMetrics(factory));
    using var metricHelper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    await using var provider = _provider(new SucceedingReceptorInvoker());
    var context = new DistributeLifecycleContext(
      [_createTestOutboxMessage()], [], provider.GetRequiredService<IServiceScopeFactory>(), new PassthroughDeserializer(), null,
      EnableLifecycleTracing: false, Metrics: metrics);

    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context);

    // The detached stage runs in the background and records under its own stage; only the inline
    // stage, which this call awaited, is asserted.
    var invocations = metricHelper.GetByName("whizbang.lifecycle.receptor.invocations")
      .Where(m => m.Value > 0 && _isInlineOutboxSeries(m))
      .ToList();
    await Assert.That(invocations.Count).IsEqualTo(1)
      .Because("one outbox message reached one receptor at the inline stage");
    await Assert.That(invocations[0].Value).IsEqualTo(1);

    var durations = metricHelper.GetByName("whizbang.lifecycle.receptor.duration")
      .Where(_isInlineOutboxSeries)
      .ToList();
    await Assert.That(durations.Count).IsEqualTo(1)
      .Because("each outbox receptor invocation records its duration, tagged with the stage and message type");
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_OutboxReceptorThrows_WithMetrics_RecordsTheErrorAndRethrowsAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleMetrics(new WhizbangMetrics(factory));
    using var metricHelper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    await using var provider = _provider(new ThrowingReceptorInvoker(new InvalidOperationException("receptor exploded")));
    var context = new DistributeLifecycleContext(
      [_createTestOutboxMessage()], [], provider.GetRequiredService<IServiceScopeFactory>(), new PassthroughDeserializer(), null,
      EnableLifecycleTracing: false, Metrics: metrics);

    await Assert.That(async () => await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context))
      .ThrowsExactly<InvalidOperationException>()
      .Because("the inline stage must not swallow an outbox receptor failure");

    var errors = metricHelper.GetByName("whizbang.lifecycle.receptor.errors")
      .Where(m => m.Value > 0 && _isInlineOutboxSeries(m))
      .ToList();
    await Assert.That(errors.Count).IsEqualTo(1)
      .Because("the failure is counted before it is rethrown");
    await Assert.That(errors[0].Tags["error_type"]).IsEqualTo(nameof(InvalidOperationException));
    var durations = metricHelper.GetByName("whizbang.lifecycle.receptor.duration")
      .Where(_isInlineOutboxSeries)
      .ToList();
    await Assert.That(durations.Count).IsEqualTo(1)
      .Because("a failed invocation still records how long it ran");
  }

  [Test]
  public async Task InvokeDistributeLifecycleStagesAsync_InboxReceptorThrows_WithoutMetrics_RethrowsTheReceptorsExceptionAsync() {
    await using var provider = _provider(new ThrowingReceptorInvoker(new InvalidOperationException("receptor exploded")));
    var context = new DistributeLifecycleContext(
      [], [_createTestInboxMessage()], provider.GetRequiredService<IServiceScopeFactory>(), new PassthroughDeserializer(), null,
      EnableLifecycleTracing: false, Metrics: null);

    await Assert.That(async () => await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("receptor exploded")
      .Because("with no metrics to record, the receptor's own exception must still be what the caller sees");
  }

  private static bool _isInlineOutboxSeries(RecordedMeasurement measurement) =>
    measurement.Tags.TryGetValue("stage", out var stage) && stage == INLINE_STAGE
    && measurement.Tags.TryGetValue("message_type", out var messageType) && messageType == MESSAGE_TYPE;

  private static ServiceProvider _provider(IReceptorInvoker invoker) {
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<JsonElement> _envelope(MessageId messageId) {
    using var document = JsonDocument.Parse("{\"value\":\"test\"}");
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = document.RootElement.Clone(),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static OutboxMessage _createTestOutboxMessage() {
    var messageId = MessageId.New();
    var envelope = _envelope(messageId);
    return new OutboxMessage {
      MessageId = messageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = envelope.Hops },
      EnvelopeType = "MessageEnvelope`1[[TestMessage, TestAssembly]]",
      MessageType = MESSAGE_TYPE
    };
  }

  private static InboxMessage _createTestInboxMessage() {
    var messageId = MessageId.New();
    return new InboxMessage {
      MessageId = messageId.Value,
      HandlerName = "TestHandler",
      Envelope = _envelope(messageId),
      EnvelopeType = "MessageEnvelope`1[[TestMessage, TestAssembly]]",
      MessageType = MESSAGE_TYPE
    };
  }

  private sealed class SucceedingReceptorInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  private sealed class ThrowingReceptorInvoker(Exception toThrow) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      throw toThrow;
  }

  private sealed class PassthroughDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) =>
      new FakeTestMessage { Value = "deserialized" };

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) =>
      new FakeTestMessage { Value = "deserialized" };

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) =>
      new FakeTestMessage { Value = "deserialized" };

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) =>
      new FakeTestMessage { Value = "deserialized" };
  }

  private sealed record FakeTestMessage {
    public required string Value { get; init; }
  }
}
