using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// The payload the framework's audit tag hands to hooks carries what identifies the audited change,
/// not the audited change itself.
/// </summary>
/// <remarks>
/// <para>
/// An audit record carries the whole body of whatever it audits, and that body is unbounded: it is
/// whatever the consumer's event contained. Left un-narrowed, the tag payload built from the record
/// carried that body too, so any application with one large event breached the tag payload size
/// threshold on every audit of it — 22.4 MB against a default of 8,192 bytes on a bulk operation —
/// and was then told to narrow a property list on an attribute it does not own.
/// </para>
/// <para>
/// Narrowing the tag payload costs the audit trail nothing. The payload is built into a separate
/// dictionary for hooks; the stored message, and the composite the audit group folds into, still
/// carry the body exactly as before.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/SystemEvents/EventAudited.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/CommandAudited.cs</code-under-test>
[Category("SystemEvents")]
public class AuditTagPayloadTests {
  private const int DEFAULT_WARNING_THRESHOLD = 8192;

  /// <summary>A body well past the threshold, standing in for a bulk event's.</summary>
  private static JsonElement _largeBody() {
    var ids = Enumerable.Range(0, 5000).Select(i => Guid.NewGuid().ToString()).ToArray();
    return JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["ItemIdentifiers"] = ids });
  }

  private static JsonElement _auditTagPayload(object message) {
    var registration = MessageTagRegistry.GetTagsFor(message.GetType()).Single(t => t.Tag == SystemTags.AUDIT);
    return registration.PayloadBuilder(message);
  }

  [Test]
  public async Task EventAudited_TagPayload_LeavesTheOriginalBodyOutAsync() {
    var audited = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventId = Guid.NewGuid(),
      OriginalEventType = "Consumer.BulkOperationStarted",
      OriginalStreamId = Guid.NewGuid().ToString(),
      OriginalStreamPosition = 7,
      OriginalBody = _largeBody(),
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = "tenant-1",
      UserId = "user-1",
    };

    var payload = _auditTagPayload(audited);

    await Assert.That(payload.TryGetProperty(nameof(EventAudited.OriginalBody), out _)).IsFalse()
      .Because("the body is unbounded; hooks identify the change, the stored record carries it");
    await Assert.That(payload.GetRawText().Length).IsLessThan(DEFAULT_WARNING_THRESHOLD)
      .Because("the framework's own tag must not breach the framework's own default threshold");
  }

  [Test]
  public async Task EventAudited_TagPayload_StillIdentifiesTheAuditedChangeAsync() {
    var streamId = Guid.NewGuid().ToString();
    var audited = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventId = Guid.NewGuid(),
      OriginalEventType = "Consumer.OrderPlaced",
      OriginalStreamId = streamId,
      OriginalStreamPosition = 3,
      OriginalBody = JsonSerializer.SerializeToElement(new { A = 1 }),
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = "tenant-2",
      UserId = "user-2",
    };

    var payload = _auditTagPayload(audited);

    await Assert.That(payload.GetProperty(nameof(EventAudited.OriginalEventType)).GetString())
      .IsEqualTo("Consumer.OrderPlaced");
    await Assert.That(payload.GetProperty(nameof(EventAudited.OriginalStreamId)).GetString()).IsEqualTo(streamId);
    await Assert.That(payload.GetProperty(nameof(EventAudited.TenantId)).GetString()).IsEqualTo("tenant-2");
    await Assert.That(payload.GetProperty(nameof(EventAudited.UserId)).GetString()).IsEqualTo("user-2");
  }

  [Test]
  public async Task CommandAudited_TagPayload_LeavesTheCommandBodyOutAsync() {
    var audited = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "Consumer.ImportEverything",
      CommandBody = _largeBody(),
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = "tenant-3",
      UserId = "user-3",
    };

    var payload = _auditTagPayload(audited);

    await Assert.That(payload.TryGetProperty(nameof(CommandAudited.CommandBody), out _)).IsFalse()
      .Because("the command twin has the same unbounded body and would otherwise be fixed by half");
    await Assert.That(payload.GetProperty(nameof(CommandAudited.CommandType)).GetString())
      .IsEqualTo("Consumer.ImportEverything");
    await Assert.That(payload.GetRawText().Length).IsLessThan(DEFAULT_WARNING_THRESHOLD);
  }
}
