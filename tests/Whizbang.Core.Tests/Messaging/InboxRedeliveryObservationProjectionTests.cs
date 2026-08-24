using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The projection is the PRODUCER side of poison detection layer 2. Every existing test for that
/// layer constructs <see cref="InboxRedeliveryObservation.ProcessingAttempts"/> by hand, which
/// exercises the detector but proves nothing about whether the value ever arrives from the store.
/// </summary>
/// <remarks>
/// <para>
/// That gap let a fail-open regression ship: the store's projection was extended to carry the inbox
/// attempt count, but the parser was never taught to read it, so the property was always null. The
/// detector requires <c>ProcessingAttempts is { } attempts &amp;&amp; attempts > 0</c>, and a null
/// fails that pattern — silently making the entire observation-count bound unreachable. A disabled
/// detector and a correctly-gated one both emit zero quarantines, so no amount of live observation
/// can tell them apart. Only a test on the parser can.
/// </para>
/// <para>
/// Absent and explicit-zero must stay DISTINCT. Null means UNMEASURED (no inbox row: never stored,
/// or already processed and removed) and must never be read as "zero failures so far". Zero means
/// measured and genuinely never attempted — the broadcast fan-out case the bound must not destroy.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
[Category("Messaging")]
public class InboxRedeliveryObservationProjectionTests {

  private static readonly Guid _messageId = Guid.Parse("01a02fc0-98d2-7059-95bb-01fefaeec5ee");

  private static InboxRedeliveryObservation _parseOne(string json) =>
    InboxRedeliveryObservation.ParseProjection(json)[0];

  [Test]
  public async Task ProjectionCarriesProcessingAttempts_WhenTheStoreReportsThemAsync() {
    var observation = _parseOne($$"""[{"m":"{{_messageId}}","o":12,"a":3}]""");

    await Assert.That(observation.ProcessingAttempts).IsEqualTo(3)
      .Because("the store projects the inbox attempt count as 'a' specifically so layer 2 can "
             + "require evidence of processing; dropping it here makes the bound unreachable and "
             + "turns a safety mechanism into a no-op that still reports success");
  }

  [Test]
  public async Task AbsentAttempts_StaysNull_NeverZeroAsync() {
    var observation = _parseOne($$"""[{"m":"{{_messageId}}","o":12}]""");

    await Assert.That(observation.ProcessingAttempts).IsNull()
      .Because("no 'a' means the store had no inbox row to read — UNMEASURED. Defaulting that to 0 "
             + "would be a fabricated reading, and quarantine decisions must never rest on a number "
             + "nobody took");
  }

  [Test]
  public async Task ExplicitNullAttempts_StaysNullAsync() {
    var observation = _parseOne($$"""[{"m":"{{_messageId}}","o":12,"a":null}]""");

    await Assert.That(observation.ProcessingAttempts).IsNull()
      .Because("a LEFT JOIN with no matching inbox row projects SQL NULL, which must survive as null "
             + "rather than throwing or collapsing to zero");
  }

  [Test]
  public async Task ExplicitZeroAttempts_IsPreservedAsZero_NotNullAsync() {
    var observation = _parseOne($$"""[{"m":"{{_messageId}}","o":12,"a":0}]""");

    await Assert.That(observation.ProcessingAttempts).IsEqualTo(0)
      .Because("zero is a MEASURED reading — the message exists in the inbox and no receptor has "
             + "attempted it, which is exactly the broadcast fan-out case the bound must spare. "
             + "Collapsing it to null would lose the distinction between measured-and-never-run and "
             + "not-measured-at-all");
  }

  [Test]
  public async Task ObservationIdentityIsStillParsedAlongsideAttemptsAsync() {
    var observation = _parseOne($$"""[{"m":"{{_messageId}}","o":12,"a":3}]""");

    await Assert.That(observation.MessageId).IsEqualTo(_messageId);
    await Assert.That(observation.ObservationCount).IsEqualTo(12)
      .Because("adding the attempts field must not disturb the fields the projection already "
             + "carried");
  }

  [Test]
  public async Task FirstSightingsAreStillExcluded_EvenWhenAttemptsArePresentAsync() {
    var parsed = InboxRedeliveryObservation.ParseProjection(
      $$"""[{"m":"{{_messageId}}","o":1,"a":5}]""");

    await Assert.That(parsed.Count).IsEqualTo(0)
      .Because("an observation count of one is a first sighting, not a redelivery; carrying an "
             + "attempt count must not smuggle it past that filter");
  }

  [Test]
  public async Task MalformedAttemptsDoesNotDiscardTheWholeObservationAsync() {
    var parsed = InboxRedeliveryObservation.ParseProjection(
      $$"""[{"m":"{{_messageId}}","o":12,"a":"not-a-number"}]""");

    await Assert.That(parsed.Count).IsEqualTo(1)
      .Because("a junk attempts value must degrade to UNMEASURED, not silently drop a genuine "
             + "redelivery observation — losing the observation entirely would disable the bound "
             + "for that message in the opposite direction");
    await Assert.That(parsed[0].ProcessingAttempts).IsNull();
  }
}
