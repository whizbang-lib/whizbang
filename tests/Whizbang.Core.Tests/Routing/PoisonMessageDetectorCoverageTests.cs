using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Targeted coverage for <see cref="PoisonMessageDetector"/>'s gate-tag fallback — reached only by
/// a <see cref="PoisonQuarantineGate"/> value outside the two defined members
/// (<see cref="PoisonQuarantineGate.Receive"/> / <see cref="PoisonQuarantineGate.Inbox"/>), which
/// the broader <see cref="PoisonMessageDetectorTests"/> suite never constructs.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/PoisonMessageDetector.cs</code-under-test>
public class PoisonMessageDetectorCoverageTests {

  [Test]
  public async Task RecordQuarantine_UnrecognizedGateValue_TagsTheMetricWithItsEnumStringInsteadOfThrowingOrMistaggingAsync() {
    // If a future gate is added to the enum without updating this switch, the fallback is what
    // keeps whizbang.message.quarantined from throwing on the quarantine-recording path itself (a
    // monitoring write must never be able to break the request it's observing) and from silently
    // mis-tagging the metric as an existing gate, which would corrupt the per-gate dashboard.
    var meterName = "Whizbang.Core.Tests.PoisonMessageDetectorCoverage." + Guid.NewGuid();
    var capturedGateTag = (object?)null;
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter.Name == meterName && instrument.Name == PoisonMessageDetector.COUNTER_NAME) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      // Passive counter: the declared receive/inbox series report at zero alongside the series
      // this quarantine counted — only the counted one carries the fallback tag.
      if (value == 0) { return; }
      foreach (var tag in tags) {
        if (tag.Key == "gate") {
          capturedGateTag = tag.Value;
        }
      }
    });
    listener.Start();

    var detector = new PoisonMessageDetector(
      Options.Create(new PoisonMessageOptions()),
      NullLogger<PoisonMessageDetector>.Instance,
      new Meter(meterName));
    var unknownGate = (PoisonQuarantineGate)99;
    var verdict = PoisonVerdict.Quarantine(PoisonQuarantineReason.MessageAgeExceeded, "unit test detail");
    var context = new PoisonEvaluationContext(
      MessageId: "msg-1", FirstEnqueuedAt: null, BrokerDeliveryCount: null, DurableObservationCount: null,
      Now: DateTimeOffset.UtcNow);

    detector.RecordQuarantine(unknownGate, verdict, context);
    listener.RecordObservableInstruments();
    listener.Dispose();

    await Assert.That(capturedGateTag?.ToString()).IsEqualTo(unknownGate.ToString())
      .Because("the fallback must tag with the enum's own string form rather than throwing or silently reusing an existing gate's tag");
  }
}
