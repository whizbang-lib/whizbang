#pragma warning disable CA1707

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// Phase H step 9 integration regression locks for the OCE storm that surfaced on a consumer
/// service's production. The unit tests in <c>Whizbang.Core.Tests</c> couldn't
/// reproduce the full bug because <see cref="InboxDispatchWorker"/>'s detached lifecycle path
/// only fires when an <see cref="ILifecycleMessageDeserializer"/> + <see cref="IReceptorInvoker"/>
/// are wired — these tests construct a minimal but real lifecycle pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Original bug: <c>_invokeInboxLifecycleStageAsync</c> used the LeaseHandle's CT for both the
/// awaited inline path AND the fire-and-forget detached path. When the lease disposed on
/// dispatch return, every parked detached task threw a first-chance <c>TaskCanceledException</c>.
/// Production saw a high sustained rate of OCEs/sec.
/// </para>
/// <para>
/// Fix: detached stages now use <c>stoppingToken</c> (worker shutdown) instead of the lease
/// token, so lease disposal doesn't cancel still-running background work.
/// </para>
/// </remarks>
[Category("Integration")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerLeaseIntegrationTests {

  // ============================================================
  // Test fakes
  // ============================================================

  public sealed record TestMessage(string Name) : IMessage;

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "integration-test-svc";
    public string HostName => "integration-test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public TaskCompletionSource<HandlerCommitRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      First.TrySetResult(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName)
      => new TestMessage("integration-test");
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope)
      => new TestMessage("integration-test");
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName)
      => new TestMessage("integration-test");
    public object DeserializeFromBytes(byte[] bytes, string messageTypeName)
      => new TestMessage("integration-test");
  }

  /// <summary>
  /// IReceptorInvoker for the integration test. The detached stage awaits a task that's racing
  /// against the inbound CT (via <see cref="Task.Delay(TimeSpan, CancellationToken)"/>) — that's
  /// how a real receptor's downstream <c>await SomeService.MethodAsync(ct)</c> registers with
  /// the cancellation token deep in the BCL's IO/timer machinery.
  /// </summary>
  /// <remarks>
  /// With the pre-fix code (detached path receives lease.Token), lease disposal on dispatch
  /// return cancels the CT, the inner Task.Delay registration fires, and a first-chance
  /// TaskCanceledException is thrown — observable on AppDomain.FirstChanceException.
  /// With the fix (detached path receives stoppingToken), the worker's CT remains alive after
  /// lease disposal, so no OCE fires until the test's CTS cancels at cleanup.
  /// </remarks>
  private sealed class HungDetachedInvoker : IReceptorInvoker {
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DetachedEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Unpark() => _completion.TrySetResult();

    public async ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      if (stage == LifecycleStage.PostInboxDetached) {
        DetachedEntered.TrySetResult();
        // Race the test-controlled completion against a long Task.Delay tied to the CT.
        // The Task.Delay is what registers with the CT — emulating a real receptor's downstream
        // BCL await. If the CT cancels, Task.Delay throws TaskCanceledException → first-chance.
        var delay = Task.Delay(TimeSpan.FromMinutes(60), cancellationToken);
        var winner = await Task.WhenAny(_completion.Task, delay).ConfigureAwait(false);
        if (winner == delay) {
          await delay.ConfigureAwait(false); // surface OCE if cancellation won
        }
      }
    }
  }

  private static InboxWork _makeWork() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    return new InboxWork {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = "Whizbang.Core.Integration.Tests.InboxDispatchWorkerLeaseIntegrationTests+TestMessage, Whizbang.Core.Integration.Tests",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 0,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  // ============================================================
  // Tests
  // ============================================================

  [Test]
  public async Task DetachedLifecycleStage_DoesNotThrowFirstChanceOceWhenLeaseDisposesAsync() {
    var firstChanceOces = new ConcurrentBag<Exception>();
    void handler(object? _, FirstChanceExceptionEventArgs e) {
      // FirstChanceException is a PROCESS-WIDE hook, so the filter must pin the exception to THIS
      // test's pipeline. Matching the whole Whizbang.Core.Workers namespace also counted OCEs thrown
      // by other workers (e.g. a PerspectiveWorker shutting down in a test running concurrently in
      // this process), which made the test fail for reasons that had nothing to do with it.
      // InboxDispatchWorker + HungDetachedInvoker are the only frames unique to this test, and the
      // regression this locks in (lease-token cancellation reaching the detached stage) throws
      // through both.
      if (e.Exception is TaskCanceledException
          && e.Exception.StackTrace is { } stack
          && (stack.Contains(nameof(InboxDispatchWorker), StringComparison.Ordinal)
              || stack.Contains(nameof(HungDetachedInvoker), StringComparison.Ordinal))) {
        firstChanceOces.Add(e.Exception);
      }
    }
    AppDomain.CurrentDomain.FirstChanceException += handler;
    try {
      // Build a minimal but real InboxDispatchWorker pipeline: real lifecycle deserializer +
      // real-shape receptor invoker (with a hung detached stage), LeaseRegistry, FakeTimeProvider.
      var instance = new FakeInstanceProvider();
      var inbox = new FakeInboxChannelWriter();
      var handlerCommit = new FakeHandlerCommitChannel();
      var failure = new FakeFailureChannel();
      var deserializer = new FakeLifecycleMessageDeserializer();
      var invoker = new HungDetachedInvoker();
      var registry = new LeaseRegistry();
      var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));

      var sp = new ServiceCollection()
        .AddScoped<IReceptorInvoker>(_ => invoker)
        .BuildServiceProvider();
      var gate = new SchemaReadyGate();
      gate.MarkReady();

      var worker = new InboxDispatchWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        instance, inbox, handlerCommit, failure, gate,
        Options.Create(new InboxDispatchWorkerOptions()),
        Options.Create(new WorkCoordinatorOptions()),
        NullLogger<InboxDispatchWorker>.Instance,
        lifecycleMessageDeserializer: deserializer,
        leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 30, MaxRenewalsPerWork = 6 }),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 60 }),
        leaseRegistry: registry,
        timeProvider: fakeTime);

      using var workerCts = new CancellationTokenSource();
      await worker.StartAsync(workerCts.Token);

      var work = _makeWork();
      await inbox.WriteAsync(work, workerCts.Token);

      // Wait for the inline path to complete (handler-commit channel receives the EventStored marker).
      // After this, the dispatch's `using var lease` scope has ended → lease disposed → CT canceled.
      var commit = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
      await Assert.That(commit.HandlerId).IsEqualTo(work.MessageId);

      // Confirm the detached stage has been entered (i.e., the parked TCS is live).
      await invoker.DetachedEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

      // Allow a brief settle period for any cascading OCE from cancellation. Without the fix,
      // lease.Token cancellation would have triggered TaskCanceledException inside the parked
      // detached invocation by now.
      await Task.Delay(300);

      await Assert.That(firstChanceOces).IsEmpty()
        .Because("detached lifecycle stages must use the worker's stoppingToken, not the lease token — lease disposal on dispatch return must not cancel still-running fire-and-forget work");

      // Cleanup: unpark the detached task before stopping the worker.
      invoker.Unpark();
      await workerCts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    } finally {
      AppDomain.CurrentDomain.FirstChanceException -= handler;
    }
  }
}
