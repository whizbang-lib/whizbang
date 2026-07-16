using System;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// TRUE end-to-end composition of the two offloads. A large ephemeral event body is uploaded to the
/// ACTUAL body store (blob/claim-check offload), wrapped as a claim envelope, and rehydrated by the ACTUAL
/// <see cref="BodyClaimRehydrator"/> — then the rehydrated FULL body flows into the Postgres event store as
/// an ephemeral event and lands full in <c>wh_event_body</c>. Proves the transport-wire offload
/// (substitute-on-publish / rehydrate-on-receive) and the storage-layer ephemeral offload compose across
/// the whole path with no interference and no truncation, using the real components (not fakes) throughout.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralBlobOffloadEndToEndTests : EFCoreTestBase {
  [Test]
  public async Task BlobOffloadedBody_Rehydrated_LandsFullInEphemeralEventBodyAsync() {
    // ── Arrange: the ACTUAL in-memory body store (blob/claim-check offload provider) ──
    var services = new ServiceCollection();
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (_, _) => new Whizbang.Offloads.InMemory.InMemoryMessageBodyStore("memory"));
    services.AddOptions<MessageBodyOffloadOptions>();
    await using var sp = services.BuildServiceProvider();
    var store = sp.GetRequiredKeyedService<IMessageBodyStore>("memory");
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    // A body far larger than any transport claim-check threshold, on an ephemeral event.
    const int bigLen = 300_000;
    var big = new string('z', bigLen);
    var eventId = (Guid)TrackedGuid.NewMedo();   // MessageId requires UUIDv7
    var streamId = Guid.NewGuid();
    using var payloadDoc = JsonDocument.Parse($$"""{"OrderId":42,"blob":"{{big}}"}""");

    var originalEnvelope = new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.From(eventId),
      Payload = payloadDoc.RootElement.Clone(),
      Hops = []
    };

    // ── Act 1: PUBLISH-side offload — serialize + upload the full body to the store, produce a claim ──
    var typeInfo = jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(originalEnvelope, typeInfo));
    var claim = await store.UploadAsync(bytes, "application/json");
    var claimEnvelope = new MessageEnvelope<BodyClaimEnvelopePayload> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.From(eventId),
      Payload = new BodyClaimEnvelopePayload(claim, "application/json", typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!),
      Hops = []
    };

    // ── Act 2: RECEIVE-side offload — rehydrate the claim back to the full original envelope ──
    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, jsonOptions, sp, CancellationToken.None);
    await Assert.That(result.IsDeadLetter).IsFalse().Because("A valid claim for a stored body must rehydrate, not dead-letter.");
    var rehydrated = (MessageEnvelope<JsonElement>)result.Envelope!;
    var rehydratedPayloadJson = rehydrated.Payload.GetRawText();
    await Assert.That(rehydratedPayloadJson.Length).IsGreaterThanOrEqualTo(bigLen)
      .Because("The rehydrated payload must be the full original body downloaded from the store.");

    // ── Act 3: the rehydrated FULL body flows into the ephemeral event store (receive path) ──
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var inbox = $$"""
      [{
        "MessageId": "{{eventId}}",
        "HandlerName": "TestHandler",
        "MessageType": "Whizbang.Tests.BigRemoteEphemeralEvent",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {"p": {{rehydratedPayloadJson}}, "h": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true,
        "Flags": 8
      }]
      """;

    await using (var storeCmd = connection.CreateCommand()) {
      storeCmd.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      storeCmd.Parameters.AddWithValue("p", inbox);
      storeCmd.Parameters.AddWithValue("inst", instanceId);
      await using var r = await storeCmd.ExecuteReaderAsync();
      while (await r.ReadAsync()) { /* drain */ }
    }
    await using (var lease = connection.CreateCommand()) {
      lease.CommandText = "UPDATE wh_inbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @id";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("id", eventId);
      await lease.ExecuteNonQueryAsync();
    }
    await using (var emit = connection.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
    }

    // ── Assert: the FULL rehydrated body is offloaded to wh_event_body (the pointer is narrow, 078) ──
    await using (var v = connection.CreateCommand()) {
      v.CommandText = @"
        SELECT length(eb.event_data ->> 'blob')
        FROM wh_event_store es LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
        WHERE es.event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      await using var r = await v.ExecuteReaderAsync();
      await Assert.That(await r.ReadAsync()).IsTrue().Because("The ephemeral event must be stored.");
      await Assert.That(r.GetInt32(0)).IsEqualTo(bigLen)
        .Because("The full 300 KB body survived blob-offload upload -> rehydrate -> ephemeral event store, intact, in wh_event_body.");
    }
  }
}
