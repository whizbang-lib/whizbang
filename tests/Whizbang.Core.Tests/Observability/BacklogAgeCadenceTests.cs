using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The backlog-age duty's cadence adapts to what it finds: the configured interval while any
/// entity shows depth, stretching to four times the interval while every entity is empty, and
/// snapping back on the first peek that finds a backlog. Each peek is a management operation
/// against the broker, so an idle fleet that peeked at full cadence forever would be the very
/// idle churn the duty exists to detect. The decision is taken inside the peek so it is provable
/// without driving the hosted loop.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/BacklogAgeWorker.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class BacklogAgeCadenceTests {
  private static readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

  [Test]
  public async Task EmptyPeeks_StretchTheIntervalUpToFourTimesAsync() {
    var peek = new RecordingPeek([_sample(depth: 0)]);
    var worker = _worker(peek);

    var intervals = new List<double>();
    for (var i = 0; i < 5; i++) {
      var found = await worker.PeekOnceAsync(CancellationToken.None);
      await Assert.That(found).IsFalse();
      intervals.Add(worker.NextInterval.TotalSeconds);
    }

    await Assert.That(intervals).IsEquivalentTo(new double[] { 10, 20, 40, 40, 40 })
      .Because("idle peeks double the delay and clamp at four times the configured interval");
  }

  [Test]
  public async Task DepthAppearing_SnapsBackToTheConfiguredIntervalAsync() {
    var peek = new RecordingPeek([_sample(depth: 0)]);
    var worker = _worker(peek);
    _ = await worker.PeekOnceAsync(CancellationToken.None);
    _ = await worker.PeekOnceAsync(CancellationToken.None);
    _ = await worker.PeekOnceAsync(CancellationToken.None);

    peek.Samples = [_sample(depth: 3)];
    var found = await worker.PeekOnceAsync(CancellationToken.None);

    await Assert.That(found).IsTrue();
    await Assert.That(worker.NextInterval).IsEqualTo(_interval)
      .Because("a backlog is the signal that the duty is needed at full cadence again");
  }

  [Test]
  public async Task DepthOnAnyEntity_CountsAsFoundAsync() {
    var peek = new RecordingPeek([_sample(depth: 0, entity: "a"), _sample(depth: 1, entity: "b")]);
    var worker = _worker(peek);

    var found = await worker.PeekOnceAsync(CancellationToken.None);

    await Assert.That(found).IsTrue();
    await Assert.That(worker.NextInterval).IsEqualTo(_interval);
  }

  [Test]
  public async Task PeekTicks_AreCountedByOutcomeAsync() {
    using var factory = new TestMeterFactory();
    var probeMetrics = new ProbeCadenceMetrics(new WhizbangMetrics(factory));
    var peek = new RecordingPeek([_sample(depth: 0)]);
    var worker = _worker(peek, probeMetrics);

    _ = await worker.PeekOnceAsync(CancellationToken.None);
    peek.Samples = [_sample(depth: 2)];
    _ = await worker.PeekOnceAsync(CancellationToken.None);

    var ticks = ProbeMeterReader.Read(factory.CreatedMeters[0], "whizbang.probes.ticks");
    await Assert.That(ticks[(ProbeCadenceMetrics.PROBE_BACKLOG_AGE, ProbeCadenceMetrics.OUTCOME_IDLE)]).IsEqualTo(1);
    await Assert.That(ticks[(ProbeCadenceMetrics.PROBE_BACKLOG_AGE, ProbeCadenceMetrics.OUTCOME_WORK)]).IsEqualTo(1);
  }

  private static BacklogSample _sample(long depth, string entity = "orders") =>
    new(entity, depth, TimeSpan.FromSeconds(1)) { Transport = "test" };

  private static BacklogAgeWorker _worker(IBacklogPeek peek, ProbeCadenceMetrics? probeMetrics = null) =>
    new([peek], [],
      Options.Create(new BacklogAgeOptions { Interval = _interval }),
      new BacklogAgeState(),
      new BacklogAgeMetrics(new WhizbangMetrics()),
      NullLogger<BacklogAgeWorker>.Instance,
      probeMetrics);

  private sealed class RecordingPeek(IReadOnlyList<BacklogSample> samples) : IBacklogPeek {
    public IReadOnlyList<BacklogSample> Samples { get; set; } = samples;
    public string TransportName => "test";
    public Task<IReadOnlyList<BacklogSample>> PeekAsync(CancellationToken cancellationToken) => Task.FromResult(Samples);
  }
}
