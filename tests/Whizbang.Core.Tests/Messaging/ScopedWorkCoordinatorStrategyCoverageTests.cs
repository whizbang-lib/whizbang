using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Two things about <see cref="ScopedWorkCoordinatorStrategy"/>'s claimed-inbox routing: the
/// invariant that keeps its only production call site handing it an empty batch, and the dedup
/// rule itself.
/// </summary>
/// <remarks>
/// <para>
/// Since the work-pump decomposition, <see cref="WorkCoordinatorFlushHelper.ExecuteFlushAsync"/>
/// returns an empty <see cref="WorkBatch"/> — claiming moved to the claim worker, and a flush now
/// only stores rows and signals <c>IInboxChannelWriter.SignalNewInboxWorkAvailable</c>. The first
/// test pins that, so a change that starts returning claimed inbox rows again without anyone
/// re-examining the routing loop fails loudly rather than leaving newly-live dedup logic behind.
/// </para>
/// <para>
/// The second test drives the dedup directly through the internal
/// <see cref="ScopedWorkCoordinatorStrategy.RouteClaimedInboxWorkToChannel"/> seam, which is what
/// the earlier note here called impossible without reflection. Asserting the rule is worth more
/// than recording that nothing reaches it: a bug in the dedup would let one scope's claimed inbox
/// work be written to the channel twice, or under another consumer's in-flight tracking, which is
/// exactly the "scoped work claimed under another scope" failure this coordinator exists to
/// prevent — and the day claiming moves back, the rule is already covered.
/// </para>
/// </remarks>
public class ScopedWorkCoordinatorStrategyCoverageTests {

  [Test]
  public async Task FlushAndGetBatchAsync_WithInboxChannelWriterWired_ReturnsEmptyInboxWorkAndNeverWritesToChannelAsync() {
    var coordinator = new NoOpWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { PartitionCount = 10_000 };
    var inboxChannelWriter = new RecordingInboxChannelWriter();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      workChannelWriter: null,
      // IWorkChannelWriter — not needed for this test
      options: options,
      logger: NullLogger<ScopedWorkCoordinatorStrategy>.Instance,
      inboxChannelWriter: inboxChannelWriter);

    sut.QueueInboxMessage(_inboxMessage());

    var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(1)
      .Because("the message really was flushed and stored — InboxWork staying empty below is a property of the claim/flush split, not of the message failing to queue.");
    await Assert.That(result.InboxWork.Count).IsEqualTo(0)
      .Because("post-Phase-H, a flush never returns claimed work — claiming is ClaimWorker's job — so the routing guard's early-return branch is the only one the current architecture can ever take.");
    await Assert.That(inboxChannelWriter.TryWriteCallCount).IsEqualTo(0)
      .Because("proving the channel writer was never invoked (not just that the result happens to be empty) is what pins the dedup loop as unreachable rather than merely untested this run — if this goes red, the loop is live again and needs its own dedup-by-IsInFlight tests.");

    await sut.DisposeAsync();
  }

  // ===== fakes =====

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "coverage-test-service";
    public string HostName { get; } = "coverage-test-host";
    public int ProcessId { get; } = 4242;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class RecordingInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public int TryWriteCallCount { get; private set; }
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) {
      TryWriteCallCount++;
      return _channel.Writer.TryWrite(work);
    }
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private static InboxMessage _inboxMessage() {
    // MessageId.From rejects anything but UUIDv7; Guid.NewGuid() is v4. MessageId.New() mints the
    // right shape, and the raw Guid for the InboxMessage row comes back off it so the two agree.
    var messageId = (Guid)MessageId.New();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new InboxMessage {
      MessageId = messageId,
      StreamId = Guid.NewGuid(),
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      HandlerName = "coverage-test-handler",
    };
  }

  // The dedup itself, driven directly. Its only production caller hands it an empty batch today
  // (the test above pins why), so without this the rule that decides whether a claimed inbox row
  // reaches the publisher would be untested until the day claiming moves back into the flush —
  // and getting it wrong means the same message dispatched twice, concurrently, which is exactly
  // the failure the in-flight set exists to prevent.
  [Test]
  public async Task RouteClaimedInboxWorkToChannel_SkipsWorkAlreadyInFlightAndWritesTheRestAsync() {
    var inFlight = (Guid)MessageId.New();
    var fresh = (Guid)MessageId.New();
    var writer = new SelectiveInFlightInboxChannelWriter(inFlight);
    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator: new NoOpWorkCoordinator(),
      instanceProvider: new FakeServiceInstanceProvider(),
      workChannelWriter: null,
      options: new WorkCoordinatorOptions { PartitionCount = 10_000 },
      logger: NullLogger<ScopedWorkCoordinatorStrategy>.Instance,
      inboxChannelWriter: writer);

    try {
      sut.RouteClaimedInboxWorkToChannel(new WorkBatch {
        OutboxWork = [],
        InboxWork = [_claimedInboxWork(inFlight), _claimedInboxWork(fresh)],
        PerspectiveWork = []
      });

      await Assert.That(writer.Written).IsEquivalentTo(new[] { fresh })
        .Because("only the row that is not already being handled may be written; writing the "
          + "in-flight one would dispatch the same message to a second handler concurrently");
    } finally {
      await sut.DisposeAsync();
    }
  }

  private static InboxWork _claimedInboxWork(Guid messageId) => new() {
    MessageId = messageId,
    Envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []),
    MessageType = "System.Text.Json.JsonElement, System.Text.Json",
    StreamId = Guid.NewGuid(),
    PartitionNumber = 1,
    Attempts = 0,
    Status = MessageProcessingStatus.Stored,
    Flags = WorkBatchOptions.None,
  };

  /// <summary>
  /// An inbox channel writer that reports a fixed set of message ids as already in flight and
  /// records everything actually written, so the dedup's two answers are told apart.
  /// </summary>
  private sealed class SelectiveInFlightInboxChannelWriter(params Guid[] inFlight) : IInboxChannelWriter {
    private readonly HashSet<Guid> _inFlight = [.. inFlight];
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    private readonly List<Guid> _written = [];

    public IReadOnlyList<Guid> Written { get { lock (_written) { return [.. _written]; } } }

    public ChannelReader<InboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      lock (_written) { _written.Add(work.MessageId); }
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryWrite(InboxWork work) {
      lock (_written) { _written.Add(work.MessageId); }
      return _channel.Writer.TryWrite(work);
    }

    public bool IsInFlight(Guid messageId) => _inFlight.Contains(messageId);
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }
}
