using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage gaps left by <c>AsbMessageHeaderReaderTests</c>: the shallow JSON fallback's two
/// "found an 'id' property but it is unusable" exits, and the malformed-JSON catch — which only
/// fires when the fast-path MessageId ApplicationProperty is ABSENT, so the fallback parser is
/// actually reached.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbMessageHeaderReader.cs</code-under-test>
public class AsbMessageHeaderReaderCoverageTests {
  private static ServiceBusReceivedMessage _build(string envelopeJson, string envelopeTypeName) {
    var props = new Dictionary<string, object> {
      [AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY] = envelopeTypeName,
      // Deliberately no MessageId ApplicationProperty: forces the shallow JSON fallback rather
      // than the fast path, which is what these tests exist to reach.
    };
    return ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString(envelopeJson),
      properties: props
    );
  }

  /// <summary>
  /// Production risk if this regresses: routing a message whose "id" is present but not a
  /// string (a numeric id, say) as if some Guid had been found would corrupt the inbox dedupe
  /// key instead of dead-lettering the un-routable message honestly.
  /// </summary>
  [Test]
  public async Task Read_IdPropertyIsNotAString_ReturnsNullAsync() {
    var message = _build(
      envelopeJson: """{"id":12345,"p":{},"h":[]}""",
      envelopeTypeName: "Whizbang.Core.Observability.MessageEnvelope`1[[Foo]]");

    var reader = new AsbMessageHeaderReader();
    var headers = reader.Read(message);

    await Assert.That(headers).IsNull();
  }

  /// <summary>
  /// Production risk if this regresses: a message whose "id" is a string but not a parseable
  /// Guid must dead-letter rather than route on a garbage identity that downstream code cannot
  /// use for dedupe or correlation.
  /// </summary>
  [Test]
  public async Task Read_IdPropertyIsAStringButNotAGuid_ReturnsNullAsync() {
    var message = _build(
      envelopeJson: """{"id":"not-a-guid","p":{},"h":[]}""",
      envelopeTypeName: "Whizbang.Core.Observability.MessageEnvelope`1[[Foo]]");

    var reader = new AsbMessageHeaderReader();
    var headers = reader.Read(message);

    await Assert.That(headers).IsNull();
  }

  /// <summary>
  /// Production risk if this regresses: with no lifted MessageId header, a body that is not
  /// valid JSON at all must be caught and turned into a dead-letter decision (null), not an
  /// unhandled exception that crashes the receive loop for every message behind it.
  /// </summary>
  [Test]
  public async Task Read_NoMessageIdHeaderAndMalformedJsonBody_ReturnsNullWithoutThrowingAsync() {
    var message = _build(
      envelopeJson: "not even close to JSON{{{{{{",
      envelopeTypeName: "Whizbang.Core.Observability.MessageEnvelope`1[[Foo]]");

    var reader = new AsbMessageHeaderReader();
    var headers = reader.Read(message);

    await Assert.That(headers).IsNull();
  }
}
