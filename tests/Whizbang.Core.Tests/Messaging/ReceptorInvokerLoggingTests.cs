using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for the structured Debug logging emitted by <see cref="ReceptorInvoker"/>
/// around every receptor invocation (EventIds 16 / 17).
/// </summary>
/// <remarks>
/// These logs are the Phase 1 diagnostic for the "receptors firing more than once"
/// investigation — they pair a <c>ReceptorFiring</c> line before each invocation with
/// a <c>ReceptorFired</c> line emitted in a <c>finally</c> block so the log still
/// reports on exception. The pair captures the seven identity fields used to group
/// firings in Aspire: ReceptorId, Stage, MessageId, StreamId, MessageType,
/// CorrelationId, SourceService, plus ElapsedMs / IsError on the post-invocation
/// record.
/// </remarks>
/// <docs>operations/observability/receptor-logging</docs>
public class ReceptorInvokerLoggingTests {

  private sealed record TestMessage(string Value) : IMessage;

  private static (ReceptorInvoker Invoker, FakeLogCollector Collector, ServiceProvider Provider) _createInvoker(
      LifecycleStage stage,
      string receptorId,
      Func<IServiceProvider, object, IMessageEnvelope, ICallerInfo?, CancellationToken, ValueTask<object?>> invoke) {
    var registry = new RecordingReceptorRegistry();
    registry.Register<TestMessage>(receptorId, stage, invoke);

    var services = new ServiceCollection();
    services.AddLogging(b => {
      b.SetMinimumLevel(LogLevel.Trace);
      b.AddFakeLogging();
    });
    var provider = services.BuildServiceProvider();
    var collector = provider.GetFakeLogCollector();

    var invoker = new ReceptorInvoker(registry, provider);
    return (invoker, collector, provider);
  }

  private static MessageEnvelope<TestMessage> _envelopeWith(
      Guid messageId,
      CorrelationId? correlationId = null,
      string? sourceService = null) {
    var hops = new List<MessageHop>();
    if (correlationId is not null || sourceService is not null) {
      hops.Add(new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = correlationId,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = sourceService ?? "test-service",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 1234
        }
      });
    }

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.From(TrackedGuid.FromExternal(messageId)),
      Payload = new TestMessage("test"),
      Hops = hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  [Test]
  public async Task InvokeAsync_EmitsFiringLogWithAllSevenFieldsAsync() {
    // Arrange
    var messageId = Guid.CreateVersion7();
    var correlationId = CorrelationId.New();
    const string sourceService = "upstream-service";
    const string receptorId = "TestReceptor";

    var (invoker, collector, provider) = _createInvoker(
      LifecycleStage.PostInboxInline,
      receptorId,
      static (_, _, _, _, _) => ValueTask.FromResult<object?>(null));

    await using (provider) {
      var envelope = _envelopeWith(messageId, correlationId, sourceService);

      // Act
      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      // Assert - one ReceptorFiring record with EventId 16 and all seven fields
      var firing = collector.GetSnapshot().FirstOrDefault(r => r.Id.Id == 16);
      await Assert.That(firing).IsNotNull();
      await Assert.That(firing!.Level).IsEqualTo(LogLevel.Debug);
      await Assert.That(firing.Id.Name).IsEqualTo("ReceptorFiring");

      var state = firing.StructuredState!.ToDictionary(p => p.Key, p => p.Value);
      await Assert.That(state["ReceptorId"]).IsEqualTo(receptorId);
      await Assert.That(state["Stage"]).IsEqualTo(nameof(LifecycleStage.PostInboxInline));
      await Assert.That(state["MessageId"]).IsEqualTo(messageId.ToString());
      await Assert.That(state["MessageType"]).IsEqualTo(typeof(TestMessage).FullName);
      await Assert.That(state["CorrelationId"]).IsEqualTo(correlationId.Value.ToString());
      await Assert.That(state["SourceService"]).IsEqualTo(sourceService);
      await Assert.That(state.ContainsKey("StreamId")).IsTrue();
    }
  }

  [Test]
  public async Task InvokeAsync_EmitsFiredLogWithElapsedMsOnSuccessAsync() {
    // Arrange
    var (invoker, collector, provider) = _createInvoker(
      LifecycleStage.PostInboxInline,
      "SuccessReceptor",
      static async (_, _, _, _, ct) => {
        await Task.Delay(5, ct).ConfigureAwait(false);
        return null;
      });

    await using (provider) {
      var envelope = _envelopeWith(Guid.CreateVersion7());

      // Act
      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      // Assert
      var fired = collector.GetSnapshot().FirstOrDefault(r => r.Id.Id == 17);
      await Assert.That(fired).IsNotNull();
      await Assert.That(fired!.Level).IsEqualTo(LogLevel.Debug);
      await Assert.That(fired.Id.Name).IsEqualTo("ReceptorFired");

      var state = fired.StructuredState!.ToDictionary(p => p.Key, p => p.Value);
      await Assert.That(state["IsError"]).IsEqualTo("False");
      // ElapsedMs is a long formatted as a string by ILogger structured state.
      var elapsedMs = long.Parse(state["ElapsedMs"]!, CultureInfo.InvariantCulture);
      await Assert.That(elapsedMs >= 0).IsTrue();
    }
  }

  [Test]
  public async Task InvokeAsync_EmitsFiredLogWithErrorFlagOnExceptionAndStillThrowsAsync() {
    // Arrange
    var (invoker, collector, provider) = _createInvoker(
      LifecycleStage.PostInboxInline,
      "ThrowingReceptor",
      static (_, _, _, _, _) => throw new InvalidOperationException("boom"));

    await using (provider) {
      var envelope = _envelopeWith(Guid.CreateVersion7());

      // Act + Assert - exception must propagate
      await Assert.That(async () => await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline))
        .Throws<InvalidOperationException>();

      // Assert - ReceptorFired still emitted in finally, with IsError=true and ExceptionType set
      var fired = collector.GetSnapshot().FirstOrDefault(r => r.Id.Id == 17);
      await Assert.That(fired).IsNotNull();
      await Assert.That(fired!.Level).IsEqualTo(LogLevel.Debug);

      var state = fired.StructuredState!.ToDictionary(p => p.Key, p => p.Value);
      await Assert.That(state["IsError"]).IsEqualTo("True");
      await Assert.That(state["ExceptionType"]).IsEqualTo(typeof(InvalidOperationException).FullName);
    }
  }

  [Test]
  [Arguments(LifecycleStage.LocalImmediateInline)]
  [Arguments(LifecycleStage.PreOutboxInline)]
  [Arguments(LifecycleStage.PostOutboxInline)]
  [Arguments(LifecycleStage.PreInboxInline)]
  [Arguments(LifecycleStage.PostInboxInline)]
  [Arguments(LifecycleStage.PrePerspectiveInline)]
  [Arguments(LifecycleStage.PostPerspectiveInline)]
  [Arguments(LifecycleStage.PostAllPerspectivesInline)]
  [Arguments(LifecycleStage.PostLifecycleInline)]
  public async Task InvokeAsync_EmitsFiringAndFiredAtEveryInlineStageAsync(LifecycleStage stage) {
    // Arrange
    var (invoker, collector, provider) = _createInvoker(
      stage,
      $"StageReceptor-{stage}",
      static (_, _, _, _, _) => ValueTask.FromResult<object?>(null));

    await using (provider) {
      var envelope = _envelopeWith(Guid.CreateVersion7());

      // Act
      await invoker.InvokeAsync(envelope, stage);

      // Assert
      var snapshot = collector.GetSnapshot();
      await Assert.That(snapshot.Any(r => r.Id.Id == 16)).IsTrue();
      await Assert.That(snapshot.Any(r => r.Id.Id == 17)).IsTrue();
    }
  }

  /// <summary>
  /// Minimal registry that invokes a user-supplied delegate when a receptor fires.
  /// Mirrors the pattern in <c>ReceptorInvokerTests.TestReceptorRegistry</c> but
  /// lets each test supply the invocation body directly.
  /// </summary>
  private sealed class RecordingReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void Register<TMessage>(
        string receptorId,
        LifecycleStage stage,
        Func<IServiceProvider, object, IMessageEnvelope, ICallerInfo?, CancellationToken, ValueTask<object?>> invoke) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }

      list.Add(new ReceptorInfo(
        MessageType: typeof(TMessage),
        ReceptorId: receptorId,
        InvokeAsync: invoke));
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : [];
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }
}
