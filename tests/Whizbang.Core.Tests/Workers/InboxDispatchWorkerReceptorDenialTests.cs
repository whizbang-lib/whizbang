// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1996: inbox delivery asks the receptor interceptors before it completes the row, and honors the
/// <see cref="DeniedAction"/> each one chose. DropQuiet completes the row (acknowledged, never retried) and only skips the
/// denied receptor; DeadLetter moves the row to the dead-letter store with no retry; Quarantine holds it there under its
/// own reason; Throw fails the attempt so the attempts bound retries it. No receptor runs for a dead-lettered,
/// quarantined or failed message.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDispatchWorker.cs</code-under-test>
[Category("Security")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerReceptorDenialTests {
  [Test]
  public async Task Dispatch_DropQuiet_CompletesTheRowWithoutRetryAsync() {
    // The invoker's admission check never reports a quiet drop: it is applied per receptor as that receptor would run
    var invoker = new AdmissionInvoker(InterceptorResult.Allowed);
    var h = new Harness(invoker);
    var work = _work();

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(h.Commits.All.Any(c => (c.InboxCompletion.Status & (int)MessageProcessingStatus.EventStored) != 0)).IsTrue()
      .Because("a quietly dropped receptor still acknowledges the message");
    await Assert.That(h.Failures.All).IsEmpty().Because("nothing failed, so nothing is retried");
    await Assert.That(h.DeadLetters.Moves).IsEmpty();
  }

  [Test]
  public async Task Dispatch_DeadLetter_MovesTheRowToTheDeadLetterStoreWithoutRetryAsync() {
    var invoker = new AdmissionInvoker(InterceptorResult.Deny(DeniedAction.DeadLetter, "Missing required permission: orders:approve"));
    var h = new Harness(invoker);
    var work = _work();

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var (sourceTable, sourceId, reason, errorText) = h.DeadLetters.Moves.Single();
    await Assert.That(sourceTable).IsEqualTo(DeadLetterSourceTable.INBOX);
    await Assert.That(sourceId).IsEqualTo(work.MessageId);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.ReceptorDenied);
    await Assert.That(errorText).Contains("orders:approve");
    await Assert.That(h.Failures.All).IsEmpty().Because("a dead-lettered denial never enters the retry loop");
    await Assert.That(h.Commits.All).IsEmpty().Because("the row is moved, never completed");
    await Assert.That(h.Inbox.Released).Contains(work.MessageId);
    await Assert.That(invoker.Invocations).IsEqualTo(0).Because("no receptor runs for a denied message");
  }

  [Test]
  public async Task Dispatch_Quarantine_HoldsTheRowUnderItsOwnReasonAsync() {
    var invoker = new AdmissionInvoker(InterceptorResult.Deny(DeniedAction.Quarantine, "under review"));
    var h = new Harness(invoker);
    var work = _work();

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(h.DeadLetters.Moves.Single().Reason).IsEqualTo(MessageFailureReason.ReceptorQuarantined);
    await Assert.That(h.Failures.All).IsEmpty();
    await Assert.That(h.Commits.All).IsEmpty();
    await Assert.That(invoker.Invocations).IsEqualTo(0);
  }

  [Test]
  public async Task Dispatch_Throw_FailsTheAttemptForTheRetryPolicyAsync() {
    var invoker = new AdmissionInvoker(InterceptorResult.Deny(DeniedAction.Throw, "try later"));
    var h = new Harness(invoker);
    var work = _work();

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var failure = h.Failures.All.Single();
    await Assert.That(failure.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(failure.Error).Contains("try later");
    await Assert.That(h.Commits.All).IsEmpty().Because("a completed row could not be retried");
    await Assert.That(h.DeadLetters.Moves).IsEmpty();
    await Assert.That(invoker.Invocations).IsEqualTo(0);
  }

  [Test]
  public async Task Dispatch_DeadLetter_NoDeadLetterStore_FailsTheRowInsteadOfCompletingItAsync() {
    var invoker = new AdmissionInvoker(InterceptorResult.Deny(DeniedAction.DeadLetter, "denied"));
    var h = new Harness(invoker, NullDeadLetterStore.Instance);
    var work = _work();

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(h.Commits.All).IsEmpty();
    await Assert.That(h.Failures.All.Single().Reason).IsEqualTo(MessageFailureReason.ReceptorDenied)
      .Because("without a store the row keeps its body and the attempts bound ends it");
  }

  [Test]
  public async Task Dispatch_Allowed_RunsTheStagesAndCompletesAsync() {
    var invoker = new AdmissionInvoker(InterceptorResult.Allowed);
    var h = new Harness(invoker);
    var work = _work();

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(invoker.Invocations).IsGreaterThan(0);
    await Assert.That(invoker.AdmissionStages).IsEquivalentTo([
      LifecycleStage.PreInboxInline, LifecycleStage.PreInboxDetached,
      LifecycleStage.PostInboxInline, LifecycleStage.PostInboxDetached]);
    await Assert.That(h.DeadLetters.Moves).IsEmpty();
  }

  private static InboxWork _work() {
    var messageId = (Guid)TrackedGuid.New();
    return new InboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonElement.Parse($$"""{"Id":"{{TrackedGuid.New()}}"}"""),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      MessageType = TypeNameFormatter.Format(typeof(InboxDenialProbeEvent)),
      StreamId = (Guid)TrackedGuid.New(),
      PartitionNumber = 1,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  /// <summary>An invoker whose admission check answers a fixed result; counts the stages it is asked to run.</summary>
  private sealed class AdmissionInvoker(InterceptorResult admission) : IReceptorInvoker {
    private int _invocations;
    public int Invocations => Volatile.Read(ref _invocations);
    public List<LifecycleStage> AdmissionStages { get; } = [];

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _invocations);
      return ValueTask.CompletedTask;
    }

    public ValueTask<InterceptorResult> EvaluateAdmissionAsync(IMessageEnvelope envelope, IReadOnlyList<LifecycleStage> stages,
        CancellationToken cancellationToken = default) {
      AdmissionStages.AddRange(stages);
      return new ValueTask<InterceptorResult>(admission);
    }
  }

  private sealed class Harness {
    public CapturingCommitChannel Commits { get; } = new();
    public CapturingFailureChannel Failures { get; } = new();
    public CapturingDeadLetterStore DeadLetters { get; } = new();
    public ReleasingInboxChannelWriter Inbox { get; } = new();
    public InboxDispatchWorker Worker { get; }

    public Harness(IReceptorInvoker invoker, IDeadLetterStore? deadLetters = null) {
      var services = new ServiceCollection();
      services.AddSingleton(invoker);
      var sp = services.BuildServiceProvider();
      var gate = new SchemaReadyGate();
      gate.MarkReady();
      Worker = new InboxDispatchWorker(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        instanceProvider: new FakeInstanceProvider(),
        inboxChannelWriter: Inbox,
        handlerCommitChannel: Commits,
        failureChannel: Failures,
        schemaReadyGate: gate,
        options: Options.Create(new InboxDispatchWorkerOptions()),
        coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
        logger: NullLogger<InboxDispatchWorker>.Instance,
        integrityOptions: Options.Create(new StreamIntegrityOptions()),
        lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        receptorRegistry: new PermissiveReceptorRegistryQuery(),
        discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
        runtimeReceptorRegistry: NullReceptorRegistry.Instance,
        deadLetterStore: deadLetters ?? DeadLetters,
        generationProvider: new DefaultGenerationProvider());
    }
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "consumer-svc";
    public string HostName => "consumer-host";
    public int ProcessId => 12;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class ReleasingInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ConcurrentQueue<Guid> Released { get; } = new();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) => Released.Enqueue(messageId);
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class CapturingCommitChannel : IInboxHandlerCommitChannel {
    public ConcurrentQueue<HandlerCommitRequest> All { get; } = new();
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      All.Enqueue(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CapturingFailureChannel : IFailureChannel {
    public ConcurrentQueue<MessageFailure> All { get; } = new();
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Enqueue(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentQueue<(string SourceTable, Guid SourceId, MessageFailureReason Reason, string? ErrorText)> Moves { get; } = new();
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moves.Enqueue((sourceTable, sourceId, failureReason, errorText));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }
}

/// <summary>The inbox message the receptor denial tests deliver (outside the class for source generator discovery).</summary>
public record InboxDenialProbeEvent([property: StreamId] Guid Id) : IEvent;
