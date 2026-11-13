// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// One failing receptor in a Distribute stage costs only its own message (#2310). A flush coalesces messages from many
/// streams, so before this a single throw skipped the stage for every message after it in the batch, and the batch was
/// logged as one error that named none of them.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/LifecycleInvocationHelper.cs</code-under-test>
[Category("Messaging")]
[Category("Lifecycle")]
public class LifecycleStageFailureIsolationTests {
  private const string MESSAGE_TYPE = "IsolationProbe, ProbeAssembly";

  [Test]
  public async Task InlineStage_RunEveryReceptorAndLog_AFailingOutboxMessage_StillRunsTheRestOfTheBatchAsync() {
    var first = _outbox();
    var second = _outbox();
    var third = _outbox();
    var invoker = new FailingForInvoker(first.MessageId);
    var logger = new FakeLogger();
    await using var provider = _provider(invoker);
    var context = _context(provider, logger, [first, second, third], []) with {
      InlineFailures = InlineStageFailureHandling.RunEveryReceptorAndLog
    };

    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context);

    await Assert.That(invoker.InvokedAt(LifecycleStage.PostDistributeInline)).IsEquivalentTo(new[] { first.MessageId, second.MessageId, third.MessageId })
      .Because("the first message's failure must not skip the inline stage for the messages after it in the flush");
    var failure = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Error);
    await Assert.That(failure.Message).Contains(first.MessageId.ToString())
      .Because("the log entry must name the message whose stage failed");
    await Assert.That(failure.Message).Contains(MESSAGE_TYPE)
      .Because("the log entry must name the message type");
    await Assert.That(failure.Message).Contains(nameof(LifecycleStage.PostDistributeInline));
    await Assert.That(failure.Exception).IsSameReferenceAs(invoker.Thrown)
      .Because("the entry carries the exception the receptor threw, not a wrapper");
  }

  [Test]
  public async Task InlineStage_RunEveryReceptorAndLog_AFailingInboxMessage_StillRunsTheRestOfTheBatchAsync() {
    var first = _inbox();
    var second = _inbox();
    var invoker = new FailingForInvoker(first.MessageId);
    var logger = new FakeLogger();
    await using var provider = _provider(invoker);
    var context = _context(provider, logger, [], [first, second]) with {
      InlineFailures = InlineStageFailureHandling.RunEveryReceptorAndLog
    };

    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context);

    await Assert.That(invoker.InvokedAt(LifecycleStage.PostDistributeInline)).IsEquivalentTo(new[] { first.MessageId, second.MessageId })
      .Because("inbox messages in the same flush are isolated the same way");
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Error)).IsEqualTo(1);
  }

  [Test]
  public async Task InlineStage_RunEveryReceptorThenThrowFirst_RunsEveryMessageThenThrowsTheFirstFailureAsync() {
    var first = _outbox();
    var second = _outbox();
    var third = _outbox();
    var invoker = new FailingForInvoker(first.MessageId, second.MessageId);
    var logger = new FakeLogger();
    await using var provider = _provider(invoker);
    var context = _context(provider, logger, [first, second, third], []) with {
      InlineFailures = InlineStageFailureHandling.RunEveryReceptorThenThrowFirst
    };

    var thrown = await Assert.That(async () => await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context))
      .ThrowsExactly<InvalidOperationException>()
      .Because("the caller of a post-store stage still learns that a receptor failed, as it did before");

    await Assert.That(thrown!.Message).Contains(first.MessageId.ToString())
      .Because("the exception thrown is the first failure's own, so a caller sees what it always saw");
    await Assert.That(invoker.InvokedAt(LifecycleStage.PostDistributeInline)).IsEquivalentTo(new[] { first.MessageId, second.MessageId, third.MessageId })
      .Because("every message's receptors run before the first failure is thrown");
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Error)).IsEqualTo(2)
      .Because("each failure is logged, including the ones that are not thrown");
  }

  [Test]
  public async Task InlineStage_Default_StopsAtTheFirstFailureAsync() {
    // The pre-store stage of a flush its caller awaits can still refuse the write: the first failure stops the stage and
    // reaches the caller, and nothing after it runs. This is the default, and the outbox batch path opts out of it.
    var first = _outbox();
    var second = _outbox();
    var invoker = new FailingForInvoker(first.MessageId);
    await using var provider = _provider(invoker);
    var context = _context(provider, new FakeLogger(), [first, second], []);

    await Assert.That(async () => await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PreDistributeDetached, LifecycleStage.PreDistributeInline, context))
      .ThrowsExactly<InvalidOperationException>();

    await Assert.That(invoker.InvokedAt(LifecycleStage.PreDistributeInline)).IsEquivalentTo(new[] { first.MessageId })
      .Because("a stage that refuses the write stops at the failure; running the rest would act on messages that are not stored");
  }

  [Test]
  public async Task InlineStage_RunEveryReceptorAndLog_AMessageThatCannotBeRead_StillRunsTheRestAsync() {
    var unreadable = _outbox();
    var readable = _outbox();
    var invoker = new FailingForInvoker();
    var logger = new FakeLogger();
    await using var provider = _provider(invoker);
    var context = _context(provider, logger, [unreadable, readable], [], new FailingForDeserializer(unreadable.MessageId)) with {
      InlineFailures = InlineStageFailureHandling.RunEveryReceptorAndLog
    };

    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context);

    await Assert.That(invoker.InvokedAt(LifecycleStage.PostDistributeInline)).IsEquivalentTo(new[] { readable.MessageId })
      .Because("a payload that cannot be read costs only its own message");
    var failure = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Error);
    await Assert.That(failure.Message).Contains(unreadable.MessageId.ToString());
    await Assert.That(failure.Message).Contains(MESSAGE_TYPE);
  }

  [Test]
  public async Task InlineStage_RunEveryReceptorAndLog_ACanceledFlush_IsThrownNotLoggedAsync() {
    var first = _outbox();
    var second = _outbox();
    using var cts = new CancellationTokenSource();
    var invoker = new CancelingInvoker(cts);
    var logger = new FakeLogger();
    await using var provider = _provider(invoker);
    var context = _context(provider, logger, [first, second], []) with {
      InlineFailures = InlineStageFailureHandling.RunEveryReceptorAndLog
    };

    await Assert.That(async () => await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context, cts.Token))
      .Throws<OperationCanceledException>()
      .Because("a stopping host is not a receptor failure: the flush ends, it does not log and move on");
    await Assert.That(invoker.Calls).IsEqualTo(1);
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Error)).IsFalse();
  }

  [Test]
  public async Task InlineStage_RunEveryReceptorAndLog_AReceptorsOwnCancellation_IsAFailureAsync() {
    // A receptor that throws OperationCanceledException while the flush is NOT being canceled failed on its own (a
    // timeout of its own, a canceled call it made). That is its message's failure, not the flush's.
    var first = _outbox();
    var second = _outbox();
    var invoker = new FailingForInvoker(new OperationCanceledException("the receptor's own timeout"), first.MessageId);
    var logger = new FakeLogger();
    await using var provider = _provider(invoker);
    var context = _context(provider, logger, [first, second], []) with {
      InlineFailures = InlineStageFailureHandling.RunEveryReceptorAndLog
    };

    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context);

    await Assert.That(invoker.InvokedAt(LifecycleStage.PostDistributeInline)).IsEquivalentTo(new[] { first.MessageId, second.MessageId });
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Error)).IsEqualTo(1);
  }

  [Test]
  public async Task DetachedStage_AFailingMessage_StillRunsTheRestOfTheBatchAsync() {
    // The detached stage never affected the flush's outcome, but one failure skipped it for every later message.
    var first = _outbox();
    var second = _outbox();
    var invoker = new FailingForInvoker(first.MessageId);
    var logger = new FakeLogger();
    var detachedWork = new DetachedLifecycleWork();
    await using var provider = _provider(invoker);
    var context = _context(provider, logger, [first, second], []) with { DetachedWork = detachedWork };

    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(LifecycleStage.DistributeDetached, context);
    await detachedWork.DrainAsync(CancellationToken.None);

    await Assert.That(invoker.InvokedAt(LifecycleStage.DistributeDetached)).IsEquivalentTo(new[] { first.MessageId, second.MessageId })
      .Because("one message's detached receptor failure must not skip the stage for the rest of the batch");
    var failure = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Error);
    await Assert.That(failure.Message).Contains(first.MessageId.ToString());
  }

  [Test]
  public async Task DetachedStage_AFailingInboxMessage_StillRunsTheRestOfTheBatchAsync() {
    var first = _inbox();
    var second = _inbox();
    var invoker = new FailingForInvoker(first.MessageId);
    var detachedWork = new DetachedLifecycleWork();
    await using var provider = _provider(invoker);
    var context = _context(provider, new FakeLogger(), [], [first, second]) with { DetachedWork = detachedWork };

    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(LifecycleStage.DistributeDetached, context);
    await detachedWork.DrainAsync(CancellationToken.None);

    await Assert.That(invoker.InvokedAt(LifecycleStage.DistributeDetached)).IsEquivalentTo(new[] { first.MessageId, second.MessageId });
  }

  [Test]
  public async Task StrategyFlush_PostDistributeInlineFailure_RunsEveryMessageThenThrowsTheFirstAsync() {
    // A strategy's flush (immediate, scoped, interval, batch) stores first, then runs PostDistributeInline. The write is
    // already done, so one message's receptor failure must not skip the stage for the others; the flush still throws
    // the first failure, as it always has, so its caller learns of it.
    var first = _outbox();
    var second = _outbox();
    var invoker = new FailingForInvoker(first.MessageId);
    var coordinator = new Whizbang.Core.Tests.Workers.NoOpWorkCoordinator();
    await using var provider = _flushProvider(invoker, coordinator);

    await Assert.That(async () => await WorkCoordinatorFlushHelper.ExecuteFlushAsync(_flushContext(provider, [first, second]), default))
      .ThrowsExactly<InvalidOperationException>();

    await Assert.That(coordinator.StoredOutboxMessages.Count).IsEqualTo(2)
      .Because("the post-store stage runs after both messages are stored");
    await Assert.That(invoker.InvokedAt(LifecycleStage.PostDistributeInline)).IsEquivalentTo(new[] { first.MessageId, second.MessageId })
      .Because("the second message's post-store receptors still run when the first message's fail");
  }

  [Test]
  public async Task StrategyFlush_PreDistributeInlineFailure_StopsAndStoresNothingAsync() {
    // Locks the design choice: before the store, an inline receptor can refuse the flush its caller awaits. The first
    // failure stops the stage and nothing is stored, as before #2310.
    var first = _outbox();
    var second = _outbox();
    var invoker = new FailingForInvoker(first.MessageId);
    var coordinator = new Whizbang.Core.Tests.Workers.NoOpWorkCoordinator();
    await using var provider = _flushProvider(invoker, coordinator);

    await Assert.That(async () => await WorkCoordinatorFlushHelper.ExecuteFlushAsync(_flushContext(provider, [first, second]), default))
      .ThrowsExactly<InvalidOperationException>();

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(0);
    await Assert.That(invoker.InvokedAt(LifecycleStage.PreDistributeInline)).IsEquivalentTo(new[] { first.MessageId });
  }

  private static ServiceProvider _flushProvider(IReceptorInvoker invoker, IWorkCoordinator coordinator) {
    var services = new ServiceCollection();
    services.AddScoped(_ => invoker);
    services.AddSingleton(coordinator);
    return services.BuildServiceProvider();
  }

  private static FlushContext _flushContext(ServiceProvider provider, OutboxMessage[] outbox) =>
    new(
      Coordinator: null,
      ScopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      InstanceProvider: new FakeInstanceProvider(),
      Options: new WorkCoordinatorOptions { PartitionCount = 4 },
      OutboxMessages: outbox,
      InboxMessages: [],
      OutboxCompletions: [],
      InboxCompletions: [],
      OutboxFailures: [],
      InboxFailures: [],
      LifecycleMessageDeserializer: new FailingForDeserializer(),
      Logger: new FakeLogger(),
      TracingOptions: null,
      LifecycleMetrics: null,
      WorkChannelWriter: null,
      PendingAuditMessages: null);

  // ========================================
  // Helpers
  // ========================================

  private static ServiceProvider _provider(IReceptorInvoker invoker) {
    var services = new ServiceCollection();
    services.AddScoped(_ => invoker);
    return services.BuildServiceProvider();
  }

  private static DistributeLifecycleContext _context(
      ServiceProvider provider,
      ILogger logger,
      OutboxMessage[] outbox,
      InboxMessage[] inbox,
      ILifecycleMessageDeserializer? deserializer = null) =>
    new(outbox, inbox, provider.GetRequiredService<IServiceScopeFactory>(), deserializer ?? new FailingForDeserializer(), logger,
      EnableLifecycleTracing: false);

  private static MessageEnvelope<JsonElement> _envelope(MessageId messageId) {
    using var document = JsonDocument.Parse($"{{\"id\":\"{messageId.Value}\"}}");
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = document.RootElement.Clone(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static OutboxMessage _outbox() {
    var messageId = MessageId.New();
    return new OutboxMessage {
      MessageId = messageId.Value,
      Destination = "isolation-topic",
      Envelope = _envelope(messageId),
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = [] },
      EnvelopeType = "MessageEnvelope`1[[IsolationProbe, ProbeAssembly]]",
      MessageType = MESSAGE_TYPE
    };
  }

  private static InboxMessage _inbox() {
    var messageId = MessageId.New();
    return new InboxMessage {
      MessageId = messageId.Value,
      HandlerName = "IsolationHandler",
      Envelope = _envelope(messageId),
      EnvelopeType = "MessageEnvelope`1[[IsolationProbe, ProbeAssembly]]",
      MessageType = MESSAGE_TYPE
    };
  }

  private sealed record IsolationProbe : IMessage;

  /// <summary>Records each (stage, message) it is asked to run and throws for the messages it was told to fail.</summary>
  private sealed class FailingForInvoker : IReceptorInvoker {
    private readonly HashSet<Guid> _failFor;
    private readonly Exception? _toThrow;
    private readonly ConcurrentQueue<(LifecycleStage Stage, Guid MessageId)> _invoked = new();

    public FailingForInvoker(params Guid[] failFor) => _failFor = [.. failFor];

    public FailingForInvoker(Exception toThrow, params Guid[] failFor) : this(failFor) => _toThrow = toThrow;

    public Exception? Thrown { get; private set; }

    public Guid[] InvokedAt(LifecycleStage stage) => [.. _invoked.Where(i => i.Stage == stage).Select(i => i.MessageId)];

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      var messageId = envelope.MessageId.Value;
      _invoked.Enqueue((stage, messageId));
      if (_failFor.Contains(messageId)) {
        var thrown = _toThrow ?? new InvalidOperationException($"receptor failed for {messageId}");
        Thrown ??= thrown;
        throw thrown;
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Cancels the flush's token from inside the first receptor, the way a stopping host does.</summary>
  private sealed class CancelingInvoker(CancellationTokenSource cts) : IReceptorInvoker {
    public int Calls { get; private set; }

    public async ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      if (stage != LifecycleStage.PostDistributeInline) {
        return;
      }
      Calls++;
      await cts.CancelAsync();
      cancellationToken.ThrowIfCancellationRequested();
    }
  }

  /// <summary>Reads every payload as an <see cref="IsolationProbe"/>, except the ones it was told cannot be read.</summary>
  private sealed class FailingForDeserializer(params Guid[] unreadable) : ILifecycleMessageDeserializer {
    private readonly HashSet<Guid> _unreadable = [.. unreadable];

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => _read(envelope.MessageId.Value);
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => _read(envelope.MessageId.Value);
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => new IsolationProbe();
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => _read(jsonElement.GetProperty("id").GetGuid());

    private IsolationProbe _read(Guid messageId) =>
      _unreadable.Contains(messageId) ? throw new JsonException($"cannot read {messageId}") : new IsolationProbe();
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "IsolationService";
    public string HostName => "isolation-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
