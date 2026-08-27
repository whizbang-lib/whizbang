using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The drain worker must report what it fetched, because it is the only place the attempt counts
/// exist on the stream-id path.
/// </summary>
/// <remarks>
/// The claim returns stream ids and never sees a row, so <c>AdaptiveClaimWindow</c> observed zero
/// churn for the life of the process and never adapted — a deployment using stream parallelism
/// logged not one window resize while rows in the same inboxes reached attempt 21. If this report
/// stops happening the window silently goes blind again, with no other symptom.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDrainWorker.cs</code-under-test>
[Category("Workers")]
public class ChurnReportingWiringTests {

  [Test]
  public async Task AWorkerWithNoFeedbackSeamStillRunsAsync() {
    // Optional dependency: a host constructing the worker directly must still start, falling back
    // to the previous unmeasured behavior rather than failing.
    var w = _worker(feedback: null);
    await Assert.That(w).IsNotNull();
  }

  [Test]
  public async Task FetchedAttemptsReachTheFeedbackSeamAsync() {
    var feedback = new ClaimChurnFeedback();
    var w = _worker(feedback);

    // Drive the same helper the fetch sites call.
    w.ReportChurnForTest([_row(1), _row(5), _row(1), _row(12)]);

    var (observed, reclaimed) = feedback.Take();
    await Assert.That(observed).IsEqualTo(4);
    await Assert.That(reclaimed).IsEqualTo(2)
      .Because("attempts 5 and 12 are rows already held and not finished — the exact evidence the "
             + "window halves on, and the evidence the claim path cannot see for itself");
  }

  [Test]
  public async Task AnEmptyFetchReportsNothingAsync() {
    var feedback = new ClaimChurnFeedback();
    _worker(feedback).ReportChurnForTest([]);

    var (observed, _) = feedback.Take();
    await Assert.That(observed).IsEqualTo(0)
      .Because("a fetch that returned nothing is not evidence the queue is clean; it is no evidence "
             + "at all, and recording it as a clean cycle would license the window to grow");
  }

  [Test]
  public async Task ReportingIsSafeWithoutASeamAsync() {
    var w = _worker(feedback: null);

    await Assert.That(() => w.ReportChurnForTest([_row(3)])).ThrowsNothing()
      .Because("the seam is optional; reporting into a null one must be a no-op rather than a "
             + "NullReferenceException on the hot fetch path");
  }

  private static InboxBatchRow _row(int attempts) => new() {
    MessageId = Guid.NewGuid(),
    StreamId = Guid.NewGuid(),
    HandlerName = "H",
    MessageType = "T",
    EventData = "{}",
    Metadata = "{}",
    Attempts = attempts,
  };

  private static InboxDrainWorker _worker(ClaimChurnFeedback? feedback) {
    var sp = new ServiceCollection().BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _Instance(), new _Drain(), new _Inbox(), gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      new JsonSerializerOptions(),
      NullLogger<InboxDrainWorker>.Instance,
      feedback);
  }

  private sealed class _Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => ServiceInstanceInfo.Unknown;
  }
  private sealed class _Drain : IInboxDrainChannel {
    private readonly Channel<Guid> _c = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _c.Reader;
    public ValueTask WriteAsync(Guid s, CancellationToken ct = default) => _c.Writer.WriteAsync(s, ct);
    public bool TryWrite(Guid s) => _c.Writer.TryWrite(s);
    public void Complete() => _c.Writer.Complete();
  }
  private sealed class _Inbox : IInboxChannelWriter {
    private readonly Channel<InboxWork> _c = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _c.Reader;
    public ValueTask WriteAsync(InboxWork w, CancellationToken ct = default) => _c.Writer.WriteAsync(w, ct);
    public bool TryWrite(InboxWork w) => _c.Writer.TryWrite(w);
    public bool IsInFlight(Guid id) => false;
    public void RemoveInFlight(Guid id) { }
    public bool ShouldRenewLease(Guid id) => false;
    public void Complete() => _c.Writer.Complete();
    public void SignalNewInboxWorkAvailable() { }
    public event Action? OnNewInboxWorkAvailable { add { } remove { } }
  }
}
