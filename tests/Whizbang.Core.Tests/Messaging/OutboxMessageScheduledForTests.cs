using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the producer-side carrier for forward-scheduled delivery on
/// <see cref="OutboxMessage"/>. The pickup path (mig 040 FetchOutboxInboxBatch),
/// the NOTIFY wake (mig 049 NotifyScheduledRetryDue), and the storage column
/// (<c>wh_outbox.scheduled_for</c>) already exist. The dispatcher exposes
/// <see cref="Whizbang.Core.Dispatch.DispatchOptions.ScheduledFor"/>. Between
/// the API surface and the SQL row, the value has to ride on the OutboxMessage
/// record that the work coordinator serializes into <c>store_outbox_messages</c>'
/// <c>p_messages</c> JSONB. This test pins that the carrier property exists.
/// </summary>
[Category("Unit")]
public class OutboxMessageScheduledForTests {

  [Test]
  public async Task OutboxMessage_ScheduledFor_PropertyExistsAsync() {
    var prop = typeof(OutboxMessage).GetProperty("ScheduledFor",
      BindingFlags.Public | BindingFlags.Instance);

    await Assert.That(prop)
      .IsNotNull()
      .Because("OutboxMessage is the producer-side carrier serialized into store_outbox_messages' p_messages " +
               "JSONB. Without ScheduledFor on this record, the DispatchOptions.ScheduledFor value has nowhere " +
               "to ride from the dispatcher to wh_outbox.scheduled_for.");
    await Assert.That(prop!.PropertyType)
      .IsEqualTo(typeof(DateTimeOffset?))
      .Because("Mirrors DispatchOptions.ScheduledFor and the existing OutboxRecord.ScheduledFor — nullable " +
               "DateTimeOffset where null means immediate delivery (matching every OutboxMessage today).");
  }

  [Test]
  public async Task OutboxMessage_ScheduledFor_DefaultIsNullAsync() {
    // Use System.Activator to construct via the record's parameterless surface — OutboxMessage is a
    // `record` with init-only properties; an unset ScheduledFor must default to null so unchanged
    // call sites keep their "immediate dispatch" semantics unchanged.
    var prop = typeof(OutboxMessage).GetProperty("ScheduledFor",
      BindingFlags.Public | BindingFlags.Instance)
      ?? throw new InvalidOperationException(
        "ScheduledFor property does not yet exist on OutboxMessage — see OutboxMessage_ScheduledFor_PropertyExistsAsync for the gap rationale.");

    // Build a minimal OutboxMessage via the required init members. Use object initializer through
    // reflection-less construction so we exercise the actual record-init contract.
    var sample = new OutboxMessage {
      MessageId = Guid.NewGuid(),
      Envelope = null!,           // not under test — left null so the property check is the only assertion
      Metadata = null!,
      EnvelopeType = "x",
      MessageType = "y"
    };
    var defaultValue = prop.GetValue(sample);
    await Assert.That(defaultValue)
      .IsNull()
      .Because("Default OutboxMessage construction must leave ScheduledFor null so the producer path " +
               "preserves the existing immediate-dispatch semantics for every call site that didn't opt in.");
  }
}
