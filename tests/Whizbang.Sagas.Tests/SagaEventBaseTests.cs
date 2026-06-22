using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks defaults for the no-custom-base saga event base. Every event
/// emitted by a <c>[Saga("Name")]</c> consumer (no custom <c>TBase</c>)
/// derives from this class — its defaults are what every such consumer
/// gets out of the box.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaEventBaseTests {

  [Test]
  public async Task Constructor_PopulatesNonEmptyMessageIdAsync() {
    var evt = new SagaEventBase();

    await Assert.That(evt.MessageId).IsNotEqualTo(Guid.Empty)
      .Because("MessageId is the inbox dedup key; defaulting to Guid.Empty would collapse every event to a single global identity.");
  }

  [Test]
  public async Task Constructor_PopulatesMessageIdToUuidv7Async() {
    // UUIDv7 has version=7 in the high nibble of byte 6.
    var evt = new SagaEventBase();
    var bytes = evt.MessageId.ToByteArray(bigEndian: true);
    var version = (bytes[6] & 0xF0) >> 4;

    await Assert.That(version).IsEqualTo(7)
      .Because("TrackedGuid.NewMedo() emits UUIDv7 — sortable, time-prefixed. A degradation to v4 would lose the database-friendly ordering that justified picking it.");
  }

  [Test]
  public async Task Constructor_TwoInstances_HaveDifferentMessageIdsAsync() {
    var a = new SagaEventBase();
    var b = new SagaEventBase();

    await Assert.That(a.MessageId).IsNotEqualTo(b.MessageId)
      .Because("Each event must have its own identity — collision would break the inbox dedup contract.");
  }

  [Test]
  public async Task Constructor_PopulatesOccurredAtToApproximatelyNowAsync() {
    var before = DateTimeOffset.UtcNow.AddSeconds(-1);
    var evt = new SagaEventBase();
    var after = DateTimeOffset.UtcNow.AddSeconds(1);

    await Assert.That(evt.OccurredAt >= before).IsTrue();
    await Assert.That(evt.OccurredAt <= after).IsTrue();
  }

  [Test]
  public async Task Constructor_CorrelationAndCausationAreNullByDefaultAsync() {
    var evt = new SagaEventBase();

    await Assert.That(evt.CorrelationId).IsNull()
      .Because("Defaulting these to anything but null would forge a tracing claim the framework can't substantiate.");
    await Assert.That(evt.CausationId).IsNull();
  }

  [Test]
  public async Task Constructor_OperationNameIsNullByDefaultAsync() {
    var evt = new SagaEventBase();

    await Assert.That(evt.OperationName).IsNull()
      .Because("OperationName is opt-in tagging; a non-null default would silently put the framework into every consumer's telemetry pipeline.");
  }

  [Test]
  public async Task ImplementsIEventAsync() {
    var evt = new SagaEventBase();

    await Assert.That(evt is Whizbang.Core.IEvent).IsTrue()
      .Because("The dispatcher's PublishAsync<TEvent> constraint requires IEvent; if SagaEventBase ever drops it, every [Saga(\"Name\")] consumer breaks at compile time.");
  }
}
