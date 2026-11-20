// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// #2323: a stored row's metadata leaves out the message id when it equals the row's event id, so the metadata reads
/// without one and every reader takes the message id from the row.
/// </summary>
/// <docs>data/event-store#row-values</docs>
[Category("Observability")]
public class EnvelopeMetadataRowValuesTests {
  private static readonly JsonSerializerOptions _stored = JsonContextRegistry.CreateCombinedOptions();
  private static readonly Guid _eventId = Guid.Parse("01900000-0000-7000-8000-0000000023a1");
  private static readonly Guid _writtenId = Guid.Parse("01900000-0000-7000-8000-0000000023a2");

  [Test]
  public async Task Read_MetadataWithoutAMessageId_ReadsTheRest_AndTheMessageIdIsTheRowsEventIdAsync() {
    const string row = """{"Hops":[{"ts":"2026-03-01T09:30:00+00:00","co":"01900000-0000-7000-8000-0000000023b1"}],"rid":"01900000-0000-7000-8000-0000000023c1"}""";

    var metadata = (EnvelopeMetadata?)JsonSerializer.Deserialize(row, _stored.GetTypeInfo(typeof(EnvelopeMetadata)));

    await Assert.That(metadata).IsNotNull().Because("a row whose message id is its event id stores none, and still reads");
    await Assert.That(metadata!.Hops).Count().IsEqualTo(1);
    await Assert.That(EnvelopeMetadata.MessageIdOf(metadata, _eventId)).IsEqualTo(MessageId.From(_eventId))
      .Because("the message id the row left out is the row's event id");
  }

  [Test]
  public async Task MessageIdOf_AStoredMessageId_IsKept_EvenWhenItIsNotTheEventIdAsync() {
    var metadata = new EnvelopeMetadata { MessageId = MessageId.From(_writtenId), Hops = [] };

    await Assert.That(EnvelopeMetadata.MessageIdOf(metadata, _eventId)).IsEqualTo(MessageId.From(_writtenId))
      .Because("a row keeps a message id that differs from its event id, and readers return it as written");
  }

  [Test]
  public async Task MessageIdOf_NoMetadata_IsTheRowsEventIdAsync() =>
    await Assert.That(EnvelopeMetadata.MessageIdOf(null, _eventId)).IsEqualTo(MessageId.From(_eventId))
      .Because("a row read without metadata still has its event id");
}
