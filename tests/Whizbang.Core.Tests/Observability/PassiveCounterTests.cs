using System.Diagnostics;
using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The passive counter (issue #711): the process accumulates, the meter observes. Nothing is
/// pushed; at collection every series the counter holds is reported with its current cumulative
/// value, so the untagged series and every declared closed-domain series exist from construction.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/PassiveCounter.cs</code-under-test>
/// <docs>operations/observability/metrics</docs>
[Category("Shard2")]
public class PassiveCounterTests {

  [Test]
  public async Task UntaggedSeries_ExistsAtZero_BeforeAnyAddAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.A");
    using var observed = new Observed(meter);
    _ = meter.CreatePassiveCounter<long>("passive.untagged");

    observed.Collect();

    await Assert.That(observed.Readings).IsEquivalentTo([("passive.untagged", 0L, "")])
      .Because("a quiet counter reads as zero, never as a missing series");
  }

  [Test]
  public async Task Add_AccumulatesAndReportsTheCumulativeValueAtCollectionAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.B");
    using var observed = new Observed(meter);
    var counter = meter.CreatePassiveCounter<long>("passive.sum");

    counter.Add(2);
    counter.Add(3);
    observed.Collect();

    await Assert.That(observed.Readings).IsEquivalentTo([("passive.sum", 5L, "")])
      .Because("adds are not pushed; the meter reads the live total when it collects");
  }

  [Test]
  public async Task Add_WithTags_KeepsOneSeriesPerTagSet_OrderInsensitiveAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.C");
    using var observed = new Observed(meter);
    var counter = meter.CreatePassiveCounter<long>("passive.tagged");

    counter.Add(1, new KeyValuePair<string, object?>("a", "x"), new KeyValuePair<string, object?>("b", "y"));
    counter.Add(1, new KeyValuePair<string, object?>("b", "y"), new KeyValuePair<string, object?>("a", "x"));
    counter.Add(1, new KeyValuePair<string, object?>("a", "other"));
    observed.Collect();

    await Assert.That(observed.Readings).IsEquivalentTo([
      ("passive.tagged", 0L, ""),
      ("passive.tagged", 2L, "a=x,b=y"),
      ("passive.tagged", 1L, "a=other"),
    ]).Because("the same tags in any order are one series; the untagged series still exists at zero");
  }

  [Test]
  public async Task Touch_DeclaresClosedDomainSeriesAtZeroAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.D");
    using var observed = new Observed(meter);
    var counter = meter.CreatePassiveCounter<long>("passive.domain");

    counter.Touch("kind", ["alpha", "beta"]);
    counter.Touch(new TagList { { "activity", "x" }, { "verdict", "y" } });
    counter.Touch(new KeyValuePair<string, object?>("single", "s"));
    observed.Collect();

    await Assert.That(observed.Readings).IsEquivalentTo([
      ("passive.domain", 0L, ""),
      ("passive.domain", 0L, "kind=alpha"),
      ("passive.domain", 0L, "kind=beta"),
      ("passive.domain", 0L, "activity=x,verdict=y"),
      ("passive.domain", 0L, "single=s"),
    ]).Because("a dashboard panel per known value must exist before the first real count");
  }

  [Test]
  public async Task Add_WithATagListAndWithASpan_ReachTheSameSeriesAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.E");
    using var observed = new Observed(meter);
    var counter = meter.CreatePassiveCounter<long>("passive.shapes");
    ReadOnlySpan<KeyValuePair<string, object?>> span = [new("k", "v")];

    counter.Add(1, new TagList { { "k", "v" } });
    counter.Add(1, span);
    counter.Add(1, new TagList());
    counter.Add(1, ReadOnlySpan<KeyValuePair<string, object?>>.Empty);
    observed.Collect();

    await Assert.That(observed.Readings).IsEquivalentTo([
      ("passive.shapes", 2L, ""),
      ("passive.shapes", 2L, "k=v"),
    ]).Because("an empty tag set is the untagged series, whichever overload carried it");
  }

  [Test]
  public async Task UpDownCounter_GoesDownAndIsAnObservableUpDownInstrumentAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.F");
    using var observed = new Observed(meter);
    var counter = meter.CreatePassiveUpDownCounter<int>("passive.updown");

    counter.Add(3);
    counter.Add(-5);
    observed.Collect();

    await Assert.That(observed.Readings).IsEquivalentTo([("passive.updown", -2L, "")]);
    await Assert.That(counter.Instrument).IsTypeOf<ObservableUpDownCounter<int>>();
  }

  [Test]
  public async Task Counter_IsAMonotonicObservableInstrument_WithNameAndMeterAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.G");
    var counter = meter.CreatePassiveCounter<long>("passive.identity", unit: "{item}", description: "d");

    await Assert.That(counter.Instrument).IsTypeOf<ObservableCounter<long>>();
    await Assert.That(counter.Name).IsEqualTo("passive.identity");
    await Assert.That(counter.Meter).IsSameReferenceAs(meter);
    await Assert.That(counter.Instrument.Unit).IsEqualTo("{item}");
  }

  [Test]
  public async Task Add_FromManyThreads_LosesNothingAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.H");
    using var observed = new Observed(meter);
    var counter = meter.CreatePassiveCounter<long>("passive.parallel");
    var tag = new KeyValuePair<string, object?>("t", "1");

    Parallel.For(0, 10_000, _ => {
      counter.Add(1);
      counter.Add(1, tag);
    });
    observed.Collect();

    await Assert.That(observed.Readings).IsEquivalentTo([
      ("passive.parallel", 10_000L, ""),
      ("passive.parallel", 10_000L, "t=1"),
    ]);
  }

  [Test]
  public async Task Create_WithoutAMeterOrAName_ThrowsAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.I");
    Meter noMeter = null!;

    await Assert.That(() => noMeter.CreatePassiveCounter<long>("x")).Throws<ArgumentNullException>();
    await Assert.That(() => meter.CreatePassiveCounter<long>(" ")).Throws<ArgumentException>();
  }

  /// <summary>Plays the exporter: collects the meter's observable instruments and records what they report.</summary>
  private sealed class Observed : IDisposable {
    private readonly MeterListener _listener = new();
    public List<(string Instrument, long Value, string Tags)> Readings { get; } = [];

    public Observed(Meter meter) {
      _listener.InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument.Meter, meter)) {
          l.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Readings.Add((i.Name, v, _format(tags))));
      _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Readings.Add((i.Name, v, _format(tags))));
      _listener.Start();
    }

    public void Collect() {
      Readings.Clear();
      _listener.RecordObservableInstruments();
    }

    public void Dispose() => _listener.Dispose();

    private static string _format(ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      var parts = new List<string>();
      foreach (var tag in tags) {
        parts.Add($"{tag.Key}={tag.Value}");
      }
      return string.Join(",", parts);
    }
  }

  [Test]
  public async Task Touch_WithAnEmptyTagList_DeclaresNothingAsync() {
    using var meter = new Meter("Whizbang.Tests.PassiveCounter.E");
    using var observed = new Observed(meter);
    var counter = meter.CreatePassiveCounter<long>("passive.empty");

    counter.Touch(new TagList());
    observed.Collect();

    await Assert.That(observed.Readings).IsEquivalentTo([("passive.empty", 0L, "")])
      .Because("an empty tag list names the untagged series, which already exists; nothing new is declared");
  }

  [Test]
  public async Task TagSet_EqualityIsByKeyAndValueAsync() {
    var a = new PassiveCounter<long>.TagSet([new("kind", "alpha"), new("verdict", "y")]);
    var sameTags = new PassiveCounter<long>.TagSet([new("kind", "alpha"), new("verdict", "y")]);
    var otherValue = new PassiveCounter<long>.TagSet([new("kind", "beta"), new("verdict", "y")]);
    var fewerTags = new PassiveCounter<long>.TagSet([new("kind", "alpha")]);

    await Assert.That(a.Equals(sameTags)).IsTrue()
      .Because("two adds with the same tags must land on one series");
    await Assert.That(a.GetHashCode()).IsEqualTo(sameTags.GetHashCode());
    await Assert.That(a.Equals(otherValue)).IsFalse()
      .Because("a different tag value is a different series");
    await Assert.That(a.Equals(fewerTags)).IsFalse()
      .Because("a subset of the tags is a different series");
    PassiveCounter<long>.TagSet? missing = null;
    object boxed = sameTags;
    await Assert.That(a.Equals(missing)).IsFalse();
    await Assert.That(a.Equals(boxed)).IsTrue();
    await Assert.That(a.Equals("not a tag set")).IsFalse();
  }
}
