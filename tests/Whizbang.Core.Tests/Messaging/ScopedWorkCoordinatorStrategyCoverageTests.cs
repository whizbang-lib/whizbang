using System.Text.Json;
using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Pins the invariant that keeps <see cref="ScopedWorkCoordinatorStrategy"/>'s private
/// <c>_routeClaimedInboxWorkToChannel</c> dedup-by-<c>IsInFlight</c> loop unreachable through its
/// only call site, <see cref="ScopedWorkCoordinatorStrategy.FlushAndGetBatchAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per <c>ai-docs/coverage-exclusions.md</c> case 3 ("one branch inside a covered member"): the
/// loop body cannot be reached without reflection on a private method, and the project's own
/// policy forbids exactly that ("Never assert an unreachable branch via reflection to force the
/// line green ... that tests the reflection, not the behaviour"). So this suite does not attempt
/// to cover the loop directly. Instead it pins the fact that makes it unreachable, so a future
/// change that makes the loop reachable again fails this test loudly instead of leaving new,
/// silently-untested dedup logic behind.
/// </para>
/// <para>
/// Since the Phase H work-pump decomposition, <see cref="WorkCoordinatorFlushHelper.ExecuteFlushAsync"/>
/// unconditionally returns an empty <see cref="WorkBatch"/> — claiming moved to <c>ClaimWorker</c>,
/// and a flush now only stores rows and signals <c>IInboxChannelWriter.SignalNewInboxWorkAvailable</c>.
/// That means the guard <c>_inboxChannelWriter is null || workBatch.InboxWork.Count == 0</c> is
/// always true in the current architecture, so the loop after it — and the IsInFlight dedup it
/// implements — can never execute in production either. If that ever regresses silently (flush
/// starts returning claimed inbox rows again without anyone re-examining the routing loop), a bug
/// in the dedup would let one scope's claimed inbox work be written to the channel twice, or under
/// another consumer's in-flight tracking, which is exactly the "scoped work claimed under another
/// scope" failure this coordinator exists to prevent.
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
      coordinator,
      instanceProvider,
      null, // IWorkChannelWriter — not needed for this test
      options,
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
}
