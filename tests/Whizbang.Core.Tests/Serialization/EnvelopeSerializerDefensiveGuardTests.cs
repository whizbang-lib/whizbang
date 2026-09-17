using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// The two guards that refuse an envelope which has already been serialized.
/// </summary>
/// <remarks>
/// Both are commented "should never happen", and their whole value is the message someone reads at
/// three in the morning when it does. A guard whose text nothing has ever rendered is a guard that
/// can be wrong about its own diagnosis, so the assertions here are on what the message names, not
/// merely on the exception type.
/// </remarks>
[Category("Core")]
[Category("Serialization")]
public class EnvelopeSerializerDefensiveGuardTests {
  /// <summary>A locally dispatched envelope: the ordinary shape, so the guards are what varies.</summary>
  private static readonly MessageDispatchContext _localContext =
    new() { Mode = DispatchModes.Local, Source = MessageSource.Local };


  /// <summary>
  /// An envelope whose payload is already a <see cref="JsonElement"/> is refused, and says so.
  /// </summary>
  /// <remarks>
  /// This is the guard a double serialization actually trips. Note which of the two fires: the
  /// payload check runs first and reads the payload's RUNTIME type, so it catches
  /// <c>MessageEnvelope&lt;JsonElement&gt;</c> as well, because a <c>JsonElement</c> payload is a
  /// struct and its runtime type is <c>JsonElement</c> whatever the type parameter says.
  /// </remarks>
  [Test]
  public async Task SerializeEnvelope_PayloadIsAlreadyAJsonElement_RefusesAndNamesTheEnvelopeAsync() {
    var serializer = new EnvelopeSerializer();
    using var document = JsonDocument.Parse("""{"already":"serialized"}""");
    var envelope = new MessageEnvelope<JsonElement> { MessageId = MessageId.New(), Payload = document.RootElement.Clone(), Hops = [], DispatchContext = _localContext };

    var thrown = await Assert.That(() => serializer.SerializeEnvelope(envelope))
      .Throws<InvalidOperationException>()
      .Because("a JsonElement payload means the envelope has been through serialization once already, "
        + "and serializing it again would nest the wire form inside itself");

    await Assert.That(thrown!.Message).Contains("DOUBLE SERIALIZATION DETECTED")
      .Because("the message is the diagnosis: the reader has to learn that the envelope was serialized "
        + "twice rather than that some type was wrong");
    await Assert.That(thrown!.Message).Contains(envelope.MessageId.ToString())
      .Because("the message id is what ties the failure to one message in a log of thousands");
  }

  /// <summary>
  /// The payload guard subsumes the type-parameter guard, so the second one cannot be reached.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is recorded as a test rather than as a comment because it is the reason the type-parameter
  /// guard shows as uncovered and will keep doing so. <c>SerializeEnvelope</c> computes
  /// <c>payloadType = payload?.GetType() ?? typeof(TMessage)</c> and refuses on
  /// <c>payloadType == typeof(JsonElement)</c> before it ever compares
  /// <c>typeof(TMessage)</c>. <c>JsonElement</c> is a value type, so an
  /// <c>IMessageEnvelope&lt;JsonElement&gt;</c> can never hand back a null payload, and boxing one
  /// always yields runtime type <c>JsonElement</c>. Every input that satisfies the second guard
  /// therefore satisfies the first.
  /// </para>
  /// <para>
  /// The guard is left in place. It costs nothing, it is correct, and the rule here is that code is
  /// never deleted or weakened to make a coverage number move. What this test prevents is the other
  /// failure: a future test that calls the serializer with <c>MessageEnvelope&lt;JsonElement&gt;</c>,
  /// asserts <c>InvalidOperationException</c>, and is believed to cover the type-parameter guard
  /// while actually exercising the payload guard.
  /// </para>
  /// </remarks>
  [Test]
  public async Task SerializeEnvelope_JsonElementTypeParameter_TripsThePayloadGuardNotTheTypeGuardAsync() {
    var serializer = new EnvelopeSerializer();
    // default(JsonElement) is ValueKind.Undefined and is still a JsonElement at runtime.
    var envelope = new MessageEnvelope<JsonElement> { MessageId = MessageId.New(), Payload = default, Hops = [], DispatchContext = _localContext };

    var thrown = await Assert.That(() => serializer.SerializeEnvelope(envelope))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("DOUBLE SERIALIZATION DETECTED")
      .Because("the payload check reads the payload's runtime type and runs first, so it is the one "
        + "that fires even when the type parameter is what is wrong");
    await Assert.That(thrown!.Message).DoesNotContain("WRONG TYPE PARAMETER")
      .Because("the type-parameter guard is unreachable by construction: a JsonElement payload is a "
        + "struct, so its runtime type is always JsonElement and the payload guard always wins. A test "
        + "that asserted only the exception type would be believed to cover that guard and would not");
  }

  /// <summary>
  /// A strongly-typed envelope serializes, which is what makes the guards above guards rather than
  /// the whole behavior.
  /// </summary>
  [Test]
  public async Task SerializeEnvelope_StronglyTypedPayload_SerializesAsync() {
    // A resolver is supplied because the repository is source-generated and a bare
    // JsonSerializerOptions has none; what is under test here is the serializer's own shaping of the
    // envelope, not the resolution strategy a host configures.
    var serializer = new EnvelopeSerializer(
      new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
    var envelope = new MessageEnvelope<GuardProbeEvent> { MessageId = MessageId.New(), Payload = new GuardProbeEvent("value"), Hops = [], DispatchContext = _localContext };

    var serialized = serializer.SerializeEnvelope(envelope);

    await Assert.That(serialized.MessageType).Contains(nameof(GuardProbeEvent))
      .Because("the ordinary path has to keep working, or the tests above prove only that everything throws");
    await Assert.That(serialized.JsonEnvelope.Payload.GetRawText()).Contains("value")
      .Because("the payload has to survive into the wire form, which is the whole point of the method");
  }

  private sealed record GuardProbeEvent(string Name) : IEvent;
}
