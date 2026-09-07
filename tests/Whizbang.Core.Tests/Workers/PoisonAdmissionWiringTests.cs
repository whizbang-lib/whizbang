using System.Text.Json;
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
/// The admission plan as the drain worker actually applies it.
/// </summary>
/// <remarks>
/// <para>
/// Failing rows are re-claimed when their lease lapses, so they permanently occupy the working set
/// and the claim never reaches rows behind them. Measured side by side on identical framework and
/// configuration: a consumer whose set had been retried into the teens held ~10,000 leases and
/// drained ~29 rows/min with 95% of its inbox never claimed; a comparison consumer at first
/// delivery drained the same backlog at ~8,000 rows/min.
/// </para>
/// <para>
/// The property that matters most here is the livelock guard. A fetch made ENTIRELY of retried rows
/// gives a share of 1.0, and a naive gate defers all of them, admits nothing, and stops the service
/// completely — converting starvation into a full halt, which is strictly worse.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDrainWorker.cs</code-under-test>
[Category("Workers")]
public partial class PoisonAdmissionWiringTests {

  private static InboxDrainWorker _worker() {
    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _Instance(), new _Drain(), new _Inbox(), gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      new JsonSerializerOptions(),
      NullLogger<InboxDrainWorker>.Instance);
  }

  private static InboxBatchRow _rowWithError(int attempts, string? error) {
    var row = _row(attempts);
    return row with { Error = error };
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

  [Test]
  public async Task AllFreshRowsAreAdmittedAsync() {
    var plan = _worker().AdmissionPlanForTest([_row(1), _row(1), _row(1), _row(1)]);

    await Assert.That(plan.All(x => x)).IsTrue()
      .Because("healthy work must never be gated — this exists to protect it, not ration it");
  }

  [Test]
  public async Task RetriedRowsYieldWhenTheyDominateAsync() {
    // 8 retried, 2 fresh: share 0.8, past the 0.5 default.
    var rows = new List<InboxBatchRow>();
    for (var i = 0; i < 8; i++) { rows.Add(_row(6)); }
    rows.Add(_row(1));
    rows.Add(_row(1));

    var plan = _worker().AdmissionPlanForTest(rows);

    await Assert.That(plan[8]).IsTrue().Because("the fresh rows must get through");
    await Assert.That(plan[9]).IsTrue();
    await Assert.That(plan.Count(x => x)).IsLessThan(rows.Count)
      .Because("if every retried row is admitted alongside them nothing changes, and the working "
             + "set stays monopolised by rows that cannot succeed");
  }

  [Test]
  public async Task AnAllRetriedFetchStillMakesProgressAsync() {
    var rows = new List<InboxBatchRow>();
    for (var i = 0; i < 10; i++) { rows.Add(_row(7)); }

    var plan = _worker().AdmissionPlanForTest(rows);

    await Assert.That(plan.Count(x => x)).IsGreaterThanOrEqualTo(1)
      .Because("share is 1.0 here, so a naive gate defers everything and the service stops dead — "
             + "a livelock is strictly worse than the starvation being prevented");
  }

  [Test]
  public async Task TheLeastRetriedRowIsTheOneForcedThroughAsync() {
    var rows = new List<InboxBatchRow> { _row(9), _row(4), _row(8) };

    var plan = _worker().AdmissionPlanForTest(rows);

    await Assert.That(plan[1]).IsTrue()
      .Because("when progress must be forced, the row CLOSEST to succeeding is the one to pick — "
             + "forcing the most-retried row spends the scarce slot on the least likely to finish");
  }

  [Test]
  public async Task RowsPastTheCeilingAreNotReadmittedAsync() {
    // Default MaxAttempts is 10; these are done and should be retired, not re-run.
    var rows = new List<InboxBatchRow> { _row(15), _row(1), _row(1), _row(1) };

    var plan = _worker().AdmissionPlanForTest(rows);

    await Assert.That(plan[0]).IsFalse()
      .Because("rows were observed at attempts 17 and 21 against a max of 10 — re-admitting them "
             + "keeps doomed work in the set long after it should have retired");
    await Assert.That(plan[1]).IsTrue();
  }

  [Test]
  public async Task AnEmptyFetchYieldsAnEmptyPlanAsync() {
    var plan = _worker().AdmissionPlanForTest([]);
    await Assert.That(plan.Length).IsEqualTo(0);
  }

  private sealed class _Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => ServiceInstanceInfo.Unknown;
  }

  private sealed class _Drain : IInboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _c =
      System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public System.Threading.Channels.ChannelReader<Guid> Reader => _c.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _c.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _c.Writer.TryWrite(streamId);
    public void Complete() => _c.Writer.Complete();
  }

  private sealed class _Inbox : IInboxChannelWriter {
    private readonly System.Threading.Channels.Channel<InboxWork> _c =
      System.Threading.Channels.Channel.CreateUnbounded<InboxWork>();
    public System.Threading.Channels.ChannelReader<InboxWork> Reader => _c.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _c.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _c.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _c.Writer.Complete();
    public void SignalNewInboxWorkAvailable() { }
    public event Action? OnNewInboxWorkAvailable { add { } remove { } }
  }
}

public partial class PoisonAdmissionWiringTests {
  [Test]
  public async Task SafeDefault_LeaseExpiryCasualtiesAreAdmittedNotDeferredAsync() {
    // Every row is past the high-attempt threshold, but each attempt ended because a lease expired
    // (a restart, a deadlock, a timeout) and the framework stamped it so: nothing ever failed. The gate
    // must not treat them as poison and throttle the whole stream to one row per cycle.
    const string STAMP = "Attempt 4 ended without a reported outcome: lease held by instance 00000000-0000-0000-0000-000000000001 expired";
    var rows = new[] { _rowWithError(5, STAMP), _rowWithError(6, STAMP), _rowWithError(5, STAMP), _rowWithError(7, STAMP) };

    var plan = _worker().AdmissionPlanForTest(rows);

    await Assert.That(plan.Count(x => x)).IsEqualTo(rows.Length)
      .Because("abandonment-stamped rows are lease casualties: admit them all");
  }

  [Test]
  public async Task SafeDefault_RowsWithRecordedFailuresStillYieldAsync() {
    var rows = new[] { _rowWithError(5, "boom"), _rowWithError(6, "boom"), _rowWithError(5, "boom"), _rowWithError(7, "boom") };

    var plan = _worker().AdmissionPlanForTest(rows);

    await Assert.That(plan.Count(x => x)).IsEqualTo(1)
      .Because("a set saturated by rows with recorded failures yields except for the forced-progress row");
  }
}
