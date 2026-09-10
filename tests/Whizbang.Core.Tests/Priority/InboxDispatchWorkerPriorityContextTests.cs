using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Priority;

/// <summary>
/// The inbox dispatch worker enters the row's effective priority as the ambient parent for the whole handling
/// (priority step 1), so every lifecycle stage and everything a receptor dispatches from inside one sees it
/// and the producer default can inherit it. Outside a handling the ambient value is undeclared.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerPriorityContextTests {

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
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
    public ConcurrentBag<MessageFailure> Failures { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      Failures.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Records the ambient parent priority every lifecycle stage was invoked under.</summary>
  private sealed class ParentObservingInvoker : IReceptorInvoker {
    public ConcurrentBag<(LifecycleStage Stage, int Parent)> Seen { get; } = [];
    public TaskCompletionSource PostLifecycleInlineFired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      Seen.Add((stage, PriorityContext.CurrentParent));
      if (stage == LifecycleStage.PostLifecycleInline) {
        PostLifecycleInlineFired.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class PassThroughLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] payload, string messageType) => JsonDocument.Parse(payload).RootElement;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => jsonElement;
  }

  private static InboxWork _work(int priority) {
    var msgId = (Guid)TrackedGuid.NewMedo();
    return new InboxWork {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = "Some.Cross.Service.Event, Some.Contracts",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
      Priority = priority,
    };
  }

  [Test]
  public async Task Dispatch_EntersTheRowsPriorityAsTheAmbientParent_ForEveryStageAsync() {
    var invoker = new ParentObservingInvoker();
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    var sp = services.BuildServiceProvider();
    var inbox = new FakeInboxChannelWriter();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), inbox, new FakeHandlerCommitChannel(), new FakeFailureChannel(), gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new PassThroughLifecycleDeserializer());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await inbox.WriteAsync(_work(WorkPriority.BACKGROUND), cts.Token);
    await invoker.PostLifecycleInlineFired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(invoker.Seen.Count).IsGreaterThan(0);
    await Assert.That(invoker.Seen.All(s => s.Parent == WorkPriority.BACKGROUND)).IsTrue()
      .Because("every stage of handling a background row runs under that number, so what a receptor emits inherits it");
    await Assert.That(PriorityContext.CurrentParent).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("the ambient parent belongs to the handling's async flow and never leaks to the test");
  }
}
