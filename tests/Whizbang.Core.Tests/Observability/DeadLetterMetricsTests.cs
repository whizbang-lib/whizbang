using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// v0.502 slice C.11 — regression locks for <see cref="DeadLetterMetrics"/>.
/// Counter instruments + meter name are the observable contract any dashboard relies on;
/// a rename without a heads-up breaks every existing alert.
/// </summary>
public class DeadLetterMetricsTests {

  [Test]
  public async Task MeterName_IsStableAsync() {
#pragma warning disable TUnitAssertions0005
    await Assert.That(DeadLetterMetrics.METER_NAME).IsEqualTo("Whizbang.DeadLetters")
      .Because("operators alert on this meter name — renaming silently breaks dashboards");
#pragma warning restore TUnitAssertions0005
  }

  [Test]
  public async Task Counters_AreCreatedWithExpectedNamesAsync() {
    var observed = new List<string>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, _) => {
        if (instrument.Meter.Name == DeadLetterMetrics.METER_NAME) {
          observed.Add(instrument.Name);
        }
      },
    };
    listener.Start();

    var _ = new DeadLetterMetrics(new WhizbangMetrics());

    await Assert.That(observed).Contains("whizbang.dead_letters.added");
    await Assert.That(observed).Contains("whizbang.dead_letters.recovered");
    await Assert.That(observed).Contains("whizbang.dead_letters.held");
    await Assert.That(observed).Contains("whizbang.dead_letters.permanently_failed");
    await Assert.That(observed).Contains("whizbang.dead_letters.recovery_attempts");
    await Assert.That(observed).Contains("whizbang.dead_letters.generation_replay_scheduled");
  }

  [Test]
  public async Task Counters_AreNotNullAsync() {
    var metrics = new DeadLetterMetrics(new WhizbangMetrics());
    await Assert.That(metrics.Added).IsNotNull();
    await Assert.That(metrics.Recovered).IsNotNull();
    await Assert.That(metrics.Held).IsNotNull();
    await Assert.That(metrics.PermanentlyFailed).IsNotNull();
    await Assert.That(metrics.RecoveryAttempts).IsNotNull();
    await Assert.That(metrics.GenerationReplayScheduled).IsNotNull();
  }
}
