using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A handler that fails because a message it produced was over the payload limit is recorded on the
/// inbox row with its own reason and the error code, on the inline and the detached stage alike, so the
/// stored message says why it failed instead of "unknown".
/// </summary>
public partial class LifecycleExceptionInvariantTests {
  /// <summary>Records failures and completes once the expected number have arrived.</summary>
  private sealed class CountingFailureChannel(int expected) : IFailureChannel {
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<MessageFailure> _all = [];
    public Task Arrived => _arrived.Task;
    public IReadOnlyList<MessageFailure> All { get { lock (_all) { return [.. _all]; } } }
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      lock (_all) {
        _all.Add(failure);
        if (_all.Count == expected) {
          _arrived.TrySetResult();
        }
      }
      return ValueTask.CompletedTask;
    }
  }

  private static MessagePayloadTooLargeException _payloadTooLarge() => new(
    new MessagePayloadSizeContext("Ns.BigEvent, App", Guid.CreateVersion7(), null, 2000, 1000, PayloadLimitSource.Default, true));

  [Test]
  public async Task InboxDispatchWorker_StageFailsOnAnOversizedMessage_RecordsTheReasonAndCodeOnBothStagesAsync() {
    var failure = new CountingFailureChannel(expected: 2);
    // The detached stage resolves its own invoker from a fresh scope, so the container carries one too.
    var invoker = new ThrowingReceptorInvoker(_payloadTooLarge());
    var sp = new ServiceCollection().AddSingleton<IReceptorInvoker>(invoker).BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new InboxDispatchWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new FakeServiceInstanceProvider(),
      inboxChannelWriter: new FakeInboxChannelWriter(),
      handlerCommitChannel: new FakeHandlerCommitChannel(),
      failureChannel: failure,
      schemaReadyGate: gate,
      options: Options.Create(new InboxDispatchWorkerOptions { Enabled = true }),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider());
    var messageId = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var work = new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      MessageType = "TestMessage",
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
    };

    await worker.InvokeInboxLifecycleStageAsync(
      work, envelope, invoker,
      LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline, "PostInbox", CancellationToken.None);
    await failure.Arrived;

    await Assert.That(failure.All.Select(f => f.Reason)).IsEquivalentTo(
      [MessageFailureReason.MessagePayloadTooLarge, MessageFailureReason.MessagePayloadTooLarge])
      .Because("retrying produces the same oversized message, so the row must say so rather than 'unknown'");
    await Assert.That(failure.All.All(f => f.Error.Contains(MessagePayloadTooLargeException.DEFAULT_ERROR_CODE))).IsTrue()
      .Because("the error code is what an operator or a UI reads off the stored message");
  }

  [Test]
  [Arguments("direct")]
  [Arguments("inner")]
  [Arguments("aggregate")]
  public async Task ClassifyHandlerFailure_FindsTheOversizedMessageWhereverItIsWrappedAsync(string shape) {
    var cause = _payloadTooLarge();
    Exception ex = shape switch {
      "direct" => cause,
      "inner" => new InvalidOperationException("handler failed", cause),
      _ => new AggregateException(new InvalidOperationException("other"), cause),
    };

    await Assert.That(InboxDispatchWorker.ClassifyHandlerFailure(ex)).IsEqualTo(MessageFailureReason.MessagePayloadTooLarge);
  }

  [Test]
  public async Task ClassifyHandlerFailure_AnythingElse_IsUnknownAsync() {
    await Assert.That(InboxDispatchWorker.ClassifyHandlerFailure(new InvalidOperationException("x", new TimeoutException())))
      .IsEqualTo(MessageFailureReason.Unknown);
    await Assert.That(InboxDispatchWorker.ClassifyHandlerFailure(new AggregateException()))
      .IsEqualTo(MessageFailureReason.Unknown);
  }

  [Test]
  public async Task RecoveryPolicy_OversizedMessage_IsHeldForReviewNotRetriedAsync() {
    var policy = new DeadLetterRecoveryOptions().PolicyByReason[MessageFailureReason.MessagePayloadTooLarge];

    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(0)
      .Because("re-driving an oversized message sends the same bytes to the same consumer");
    await Assert.That(policy.HoldForReviewAfterExhaustion).IsTrue();
  }
}
