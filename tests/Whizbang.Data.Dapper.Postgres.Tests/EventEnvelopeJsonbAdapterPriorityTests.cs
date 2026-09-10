using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Priority step 1 in the event store's three-column form: the adapter writes the envelope's declared number into
/// the metadata column as <c>pri</c> (omitted when undeclared, the same convention as the wire) and copies it back
/// when it rebuilds a typed envelope, so an event appended with a number is read back with it and a row written
/// before the field existed is read back undeclared.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#on-the-wire</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/EventEnvelopeJsonbAdapter.cs</code-under-test>
public class EventEnvelopeJsonbAdapterPriorityTests {
  private static EventEnvelopeJsonbAdapter _adapter() => new(JsonOptionsHelper.CreateOptions());

  private static MessageEnvelope<TestEvent> _envelope(int priority) => new() {
    MessageId = MessageId.New(),
    Payload = new TestEvent { StreamId = Guid.NewGuid(), Payload = "priority" },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow,
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Priority = priority,
  };

  private static bool _tryReadPri(string metadataJson, out int value) {
    using var document = JsonDocument.Parse(metadataJson);
    if (document.RootElement.TryGetProperty("pri", out var element) && element.ValueKind == JsonValueKind.Number) {
      value = element.GetInt32();
      return true;
    }
    value = 0;
    return false;
  }

  [Test]
  public async Task ToJsonb_WithADeclaredNumber_WritesPriIntoTheMetadataAsync() {
    var jsonb = _adapter().ToJsonb(_envelope(WorkPriority.BACKGROUND));

    var present = _tryReadPri(jsonb.MetadataJson, out var value);
    await Assert.That(present).IsTrue()
      .Because("the metadata column is the envelope's only home in the three-column form; a number not written there is gone for every later read");
    await Assert.That(value).IsEqualTo(WorkPriority.BACKGROUND);
  }

  [Test]
  public async Task ToJsonb_WithAnUndeclaredNumber_OmitsPriAsync() {
    var jsonb = _adapter().ToJsonb(_envelope(WorkPriority.UNDECLARED));

    await Assert.That(_tryReadPri(jsonb.MetadataJson, out _)).IsFalse()
      .Because("undeclared costs nothing on the wire and nothing in the row; the key means somebody said");
  }

  [Test]
  public async Task RoundTrip_KeepsTheDeclaredNumberAsync() {
    var adapter = _adapter();

    var restored = adapter.FromJsonb<TestEvent>(adapter.ToJsonb(_envelope(WorkPriority.INTERACTIVE)));

    await Assert.That(restored.Priority).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("an event appended with a number is read back with it; whatever consumes the read (a replay, a rehydration) sees what the producer declared");
  }

  [Test]
  public async Task FromJsonb_WithPriInTheMetadata_RestoresItAsync() {
    var adapter = _adapter();
    var withoutNumber = adapter.ToJsonb(_envelope(WorkPriority.UNDECLARED));
    var withNumber = withoutNumber with {
      MetadataJson = "{\"pri\":250," + withoutNumber.MetadataJson[1..],
    };

    var restored = adapter.FromJsonb<TestEvent>(withNumber);

    await Assert.That(restored.Priority).IsEqualTo(250)
      .Because("the read side copies the key on its own, independent of how the row was written");
  }

  [Test]
  public async Task FromJsonb_WithoutPriInTheMetadata_StaysUndeclaredAsync() {
    var adapter = _adapter();

    var restored = adapter.FromJsonb<TestEvent>(adapter.ToJsonb(_envelope(WorkPriority.UNDECLARED)));

    await Assert.That(restored.Priority).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("a row written before the field existed reads back as it was written: undeclared, for the consumer's rules to classify");
  }
}
