using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers <see cref="InboxRecord.Scope"/> — omitted by the sibling
/// <c>RecordTypesConstructionTests.InboxRecord_FullInitialization_RoundTripsAllPropertiesAsync</c>,
/// which sets and asserts every other property but this one. The EF Core mapping
/// (<c>WhizbangModelBuilderExtensions</c>) configures this column via an expression tree — which
/// never actually invokes the property getter at runtime — so nothing else in the solution reads
/// it back through a real getter call.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/InboxRecord.cs</code-under-test>
public class InboxRecordCoverageTests {

  [Test]
  public async Task Scope_SetOnConstruction_RoundTripsAsync() {
    // This becomes the wh_inbox.scope jsonb column — the multi-tenancy filter perspectives and
    // audit queries key on. A dropped Scope here means every inbox row loses its tenant/user
    // attribution silently.
    var messageId = Guid.NewGuid();
    var scope = new PerspectiveScope { TenantId = "tenant-a", UserId = "user-a" };
    var record = new InboxRecord {
      MessageId = messageId,
      HandlerName = "SampleHandler",
      MessageType = "Sample.Type, Sample",
      MessageData = new InboxMessageData {
        MessageId = new MessageId(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
      },
      Metadata = new EnvelopeMetadata { MessageId = new MessageId(messageId), Hops = [] },
      Scope = scope,
    };

    await Assert.That(record.Scope).IsNotNull();
    await Assert.That(record.Scope!.TenantId).IsEqualTo("tenant-a");
    await Assert.That(record.Scope!.UserId).IsEqualTo("user-a");
  }

  [Test]
  public async Task Scope_NotSet_DefaultsToNullAsync() {
    var messageId = Guid.NewGuid();
    var record = new InboxRecord {
      MessageId = messageId,
      HandlerName = "SampleHandler",
      MessageType = "Sample.Type, Sample",
      MessageData = new InboxMessageData {
        MessageId = new MessageId(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
      },
      Metadata = new EnvelopeMetadata { MessageId = new MessageId(messageId), Hops = [] },
    };

    await Assert.That(record.Scope).IsNull()
      .Because("a message routed outside any tenant/user context (e.g. system-originated) has no scope, and that must read back as null, not an empty scope object.");
  }
}
