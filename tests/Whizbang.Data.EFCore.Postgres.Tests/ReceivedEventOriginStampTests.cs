using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A cross-service event for the origin-stamp suite; public so the Whizbang JSON generator emits its typed envelope contract.</summary>
public sealed record OriginStampProbeEvent([property: StreamId] Guid ProbeId, string Note) : IEvent;

/// <summary>
/// Service A publishes an event; service B receives it and stores it. B's stored event must name A as its
/// origin, with A's commit sequence (#1029). Every step runs as production runs it: A's storage form and
/// outbox-drain origin stamp, the attribute-honoring wire serialization, B's typed bind through the
/// generated envelope contract by the wire's envelope-type name, B's storage-form conversion and shared
/// row builder, and the real <c>store_inbox_messages</c> plus inbox emit chain into <c>wh_event_store</c>. The typed
/// contract used to drop <c>sid</c>/<c>sseq</c>, so B stored A's events as its own and integrity never saw them.
/// </summary>
/// <docs>resilience/stream-integrity#origin-stamp</docs>
[Category("Shard1")]
public class ReceivedEventOriginStampTests : EFCoreTestBase {

  /// <summary>Service A: the wire bytes its outbox drain publishes, and the wire's envelope-type name.</summary>
  private static (string Wire, string EnvelopeType, Guid EventId) _serviceAPublishes(
      JsonSerializerOptions options, Guid? originServiceId, long originCommitSequence) {
    var eventId = (Guid)TrackedGuid.New();
    var typed = new MessageEnvelope<OriginStampProbeEvent> {
      MessageId = MessageId.From(eventId),
      Payload = new OriginStampProbeEvent((Guid)TrackedGuid.New(), "from A"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
    };
    var serialized = new EnvelopeSerializer(options).SerializeEnvelope(typed);
    var stored = serialized.JsonEnvelope;
    var wire = originServiceId is { } origin
      ? new MessageEnvelope<JsonElement> {
        MessageId = stored.MessageId,
        Payload = stored.Payload,
        Hops = stored.Hops,
        DispatchContext = stored.DispatchContext,
        SourceServiceId = origin,
        SourceCommitSequence = originCommitSequence,
      }
      : stored;   // an older sender: no origin on the wire
    var json = JsonSerializer.Serialize(wire, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    return (json, serialized.EnvelopeType, eventId);
  }

  /// <summary>
  /// Service B: binds the wire through the generated contract, converts it to the storage form, and builds the
  /// row as the consumer workers' shared builder does: the source columns come from the received envelope.
  /// </summary>
  private static InboxMessage _serviceBReceives(JsonSerializerOptions options, string wire, string envelopeType) {
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(envelopeType, options)
      ?? throw new InvalidOperationException($"No typed contract for {envelopeType}");
    var envelope = (IMessageEnvelope<OriginStampProbeEvent>)JsonSerializer.Deserialize(wire, typeInfo)!;
    var jsonEnvelope = new EnvelopeSerializer(options).SerializeEnvelope(envelope).JsonEnvelope;
    var messageType = TypeNameFormatter.AssemblyQualifiedNameOrNull(typeof(OriginStampProbeEvent))!;
    return new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = "OriginStampProbeEventHandler",
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeType,
      MessageType = messageType,
      StreamId = envelope.Payload.ProbeId,
      IsEvent = true,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = envelope.Hops.ToList() },
      SourceServiceId = jsonEnvelope.SourceServiceId,
      SourceCommitSequence = jsonEnvelope.SourceCommitSequence,
    };
  }

  private async Task<(Guid? Origin, long? Sequence)> _storeAndReadOriginAsync(InboxMessage row, JsonSerializerOptions options) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, options);
    await coordinator.StoreInboxMessagesAsync([row], partitionCount: 100);

    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    // The inbox emit chain writes the stored event for rows this instance holds, as the work batch runs it.
    var instanceId = (Guid)TrackedGuid.New();
    await using (var lease = conn.CreateCommand()) {
      lease.CommandText = "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @id";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("id", row.MessageId);
      await lease.ExecuteNonQueryAsync();
    }
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
    }

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT origin_service_id, origin_commit_sequence FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", row.MessageId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"No wh_event_store row for {row.MessageId}");
    }
    Guid? origin = await reader.IsDBNullAsync(0) ? null : reader.GetGuid(0);
    long? sequence = await reader.IsDBNullAsync(1) ? null : reader.GetInt64(1);
    return (origin, sequence);
  }

  [Test]
  public async Task ServiceAPublishes_ServiceBsStoredEvent_NamesAAsItsOriginAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var serviceA = (Guid)TrackedGuid.New();
    var (wire, envelopeType, eventId) = _serviceAPublishes(options, serviceA, originCommitSequence: 17);

    var row = _serviceBReceives(options, wire, envelopeType);
    var (origin, sequence) = await _storeAndReadOriginAsync(row, options);

    await Assert.That(row.MessageId).IsEqualTo(eventId);
    await Assert.That(origin).IsEqualTo(serviceA)
      .Because("B's stored event must name the service that produced it; NULL means integrity cannot attribute it");
    await Assert.That(sequence).IsEqualTo(17L)
      .Because("the origin's commit sequence is what checkpoints and gap detection count against");
  }

  [Test]
  public async Task AnOlderSender_WithoutAnOrigin_IsStoredUnstamped_AsBeforeAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var (wire, envelopeType, _) = _serviceAPublishes(options, originServiceId: null, originCommitSequence: 0);

    var row = _serviceBReceives(options, wire, envelopeType);
    var (origin, sequence) = await _storeAndReadOriginAsync(row, options);

    await Assert.That(origin).IsNull()
      .Because("a wire without sid keeps today's behavior: the row is stamped with B's own id and the event stays locally originated");
    await Assert.That(sequence).IsNull();
  }
}
