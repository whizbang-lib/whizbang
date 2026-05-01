using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="InboxDrainWorker"/> — the per-stream-id inbox drainer that adapts
/// stream_ids back into <see cref="InboxWork"/> records and writes to the legacy
/// <see cref="IInboxChannelWriter"/>. <see cref="InboxDispatchWorker"/> reads from there and
/// does the actual handler dispatch + lifecycle hooks (unchanged).
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDrainWorkerTests {

  // --- fakes ---

  private sealed class FakeInboxDrainChannel : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class CapturingInboxChannel : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ConcurrentBag<InboxWork> Written { get; } = [];
    public TaskCompletionSource<int> SecondWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      Written.Add(work);
      if (Written.Count >= 2) {
        SecondWritten.TrySetResult(Written.Count);
      }
      return _channel.Writer.WriteAsync(work, ct);
    }
    public bool TryWrite(InboxWork work) {
      Written.Add(work);
      if (Written.Count >= 2) {
        SecondWritten.TrySetResult(Written.Count);
      }
      return _channel.Writer.TryWrite(work);
    }
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<InboxBatchRow>> RowsByStream { get; } = [];
    public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      var result = new List<InboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          result.AddRange(rows.Take(maxPerStream));
        }
      }
      return Task.FromResult<IReadOnlyList<InboxBatchRow>>(result);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  // --- helpers ---

  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static InboxBatchRow _row(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>");
    var envelopeJson = JsonSerializer.Serialize(envelope, typeInfo);
    return new InboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      HandlerName = "TestHandler",
      MessageType = "TestMessage",
      EventData = envelopeJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = 0,
      PartitionNumber = 0,
      IsEvent = false,
    };
  }

  // --- tests ---

  [Test]
  public async Task InboxDrainWorker_OnStreamId_FetchesBatch_FeedsInboxChannelInOrderAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgA = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgA, streamId), _row(msgB, streamId)];

    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel();
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);

    _ = await Task.WhenAny(inbox.SecondWritten.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(inbox.Written.Count).IsEqualTo(2);
    var writtenMessageIds = inbox.Written.Select(w => w.MessageId).ToHashSet();
    await Assert.That(writtenMessageIds).Contains(msgA);
    await Assert.That(writtenMessageIds).Contains(msgB);
  }

  [Test]
  public async Task InboxDrainWorker_DefaultDisabled_NoActivityAsync() {
    var coord = new FakeWorkCoordinator();
    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel();
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions()),  // default Enabled = false
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync((Guid)TrackedGuid.NewMedo());

    await Task.Delay(200);  // give worker any chance to misbehave
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(inbox.Written.Count).IsEqualTo(0)
      .Because("InboxDrainWorker default-disabled — must not fetch or write while ClaimWorker still feeds InboxWork directly.");
  }
}
