// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// <see cref="ReceivedEnvelopeReader"/> reads any valid envelope as received, and nothing that is not one (#1720).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Transports/ReceivedEnvelopeReader.cs</code-under-test>
public class ReceivedEnvelopeReaderTests {
  private static readonly JsonSerializerOptions _options = JsonContextRegistry.CreateCombinedOptions();

  [Test]
  public async Task TryRead_AValidEnvelope_ReturnsItAsReceivedAsync() {
    var id = (Guid)TrackedGuid.New();

    var envelope = ReceivedEnvelopeReader.TryRead(_body(id), _options);

    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.MessageId.Value).IsEqualTo(id);
    await Assert.That(envelope.Payload.GetProperty("reference").GetString()).IsEqualTo("R-1");
  }

  [Test]
  public async Task TryRead_OptionsThatDescribeNoEnvelope_ReadsWithTheFrameworksOwnAsync() {
    var id = (Guid)TrackedGuid.New();
    var describesNothing = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() };

    var envelope = ReceivedEnvelopeReader.TryRead(_body(id), describesNothing);

    await Assert.That(envelope!.MessageId.Value).IsEqualTo(id);
  }

  [Test]
  public async Task TryRead_OptionsWhoseResolverAnswersForAnotherType_ReadsWithTheFrameworksOwnAsync() {
    var id = (Guid)TrackedGuid.New();
    var stringInfo = _options.GetTypeInfo(typeof(string));
    var mismatched = new JsonSerializerOptions { TypeInfoResolver = new AnswersWith(stringInfo) };

    var envelope = ReceivedEnvelopeReader.TryRead(_body(id), mismatched);

    await Assert.That(envelope!.MessageId.Value).IsEqualTo(id)
      .Because("metadata for another type is no metadata for the envelope, so the framework's own is used");
  }

  /// <summary>A resolver that answers every type with one type's metadata, as a misconfigured transport's might.</summary>
  private sealed class AnswersWith(JsonTypeInfo typeInfo) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => typeInfo;
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  [Arguments("null")]
  [Arguments("""{"id": not json""")]
  [Arguments("""{"hello":1}""")]
  public async Task TryRead_ABodyThatIsNotAnEnvelope_ReturnsNullAsync(string? body) {
    await Assert.That(ReceivedEnvelopeReader.TryRead(body, _options)).IsNull();
  }

  [Test]
  public async Task TryRead_AnEnvelopeWhoseIdIsNotAMessageId_ReturnsNullAsync() {
    var id = (Guid)TrackedGuid.New();
    var body = _body(id).Replace(id.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal);

    await Assert.That(ReceivedEnvelopeReader.TryRead(body, _options)).IsNull()
      .Because("an id the message id type rejects cannot be stored or given custody under it");
  }

  [Test]
  public async Task TryRead_NullOptions_ThrowsAsync() {
    await Assert.That(() => ReceivedEnvelopeReader.TryRead("{}", null!)).Throws<ArgumentNullException>();
  }

  private static string _body(Guid id) => JsonSerializer.Serialize(new MessageEnvelope<JsonElement> {
    MessageId = MessageId.From(id),
    Payload = JsonElement.Parse("""{"reference":"R-1"}"""),
    Hops = [new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.Parse("0198f5d2-0000-7000-8000-000000000001"), ServiceName = "upstream", HostName = "host", ProcessId = 1 },
      Timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
  }, _options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
}
