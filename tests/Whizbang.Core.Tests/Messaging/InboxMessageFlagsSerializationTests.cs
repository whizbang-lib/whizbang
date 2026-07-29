using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The store SQL reads each queued inbox/outbox row's flags from the JSON key <c>Flags</c>
/// (numeric or [Flags]-string form). If the production serializer ever renames, omits, or
/// re-shapes that key, every stored row silently degrades to flags=0 — collective routing,
/// the ephemeral reaper, and the replay guards all lose their signal with no error anywhere.
/// This locks the wire contract between the C# serializer and the store SQL.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/InboxMessage.cs</code-under-test>
[Category("Messaging")]
public class InboxMessageFlagsSerializationTests {

  [Test]
  public async Task InboxMessage_SerializedWithProductionOptions_CarriesReadableFlagsKeyAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var message = new InboxMessage {
      MessageId = Guid.NewGuid(),
      HandlerName = "TestHandler",
      Envelope = null!,
      EnvelopeType = "irrelevant",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      Flags = EventFlags.Collective,
      MessageType = "irrelevant"
    };

    var typeInfo = options.GetTypeInfo(typeof(InboxMessage[]))
      ?? throw new InvalidOperationException("InboxMessage[] must be registered in the infrastructure JSON context.");
    var json = JsonSerializer.Serialize(new[] { message }, typeInfo);

    using var doc = JsonDocument.Parse(json);
    var row = doc.RootElement[0];
    await Assert.That(row.TryGetProperty("Flags", out var flags)).IsTrue()
      .Because($"the store SQL reads elem->>'Flags' — if the key is renamed or omitted the row " +
               $"persists flags=0 silently. Actual JSON: {json[..Math.Min(json.Length, 600)]}");
    var readable = flags.ValueKind == JsonValueKind.Number
      || (flags.ValueKind == JsonValueKind.String && (flags.GetString()?.Contains("Collective", StringComparison.OrdinalIgnoreCase) ?? false));
    await Assert.That(readable).IsTrue()
      .Because($"the SQL reader accepts a number or a [Flags] string containing 'Collective'; got: {flags}");
  }
}
