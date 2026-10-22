// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// An event the broker dead-lettered, that dead-letter custody imported and that recovery then re-drove, reaches
/// the receiving service's event store and its perspectives, as the same event delivered normally would (#1664).
/// </summary>
/// <remarks>
/// <para>
/// The import stores the raw wire body and the payload type, and no stream unless the broker carried a session.
/// Recovery re-emitted the row into the inbox as a message that is not an event and has no stream, so the inbox
/// copy into the event store never took it: the row was dispatched, completed and deleted, its id was recorded as
/// seen, and the dead-letter row read Recovered while nothing had been appended.
/// </para>
/// <para>
/// Real PostgreSQL throughout: the import is the coordinator's, the recovery is the recovery service the Postgres
/// driver registers, and the copy is the claim's own.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RecoveredBrokerDeadLetterEventStoreTests : EFCoreTestBase {
  private const string PERSPECTIVE = "RecoveredOrderPerspective";
  private const int EPHEMERAL_FLAG = 8;
  private const int RECOVERED = 3;
  private const int HOLD_FOR_REVIEW = 2;

  /// <summary>A domain event a perspective of the receiving service applies.</summary>
  public record RecoveredOrderPlaced([property: StreamId] Guid OrderId, string Reference) : IEvent;

  [Test]
  public async Task AnEventImportedFromTheBrokerDeadLetterQueue_AndRecovered_IsAppended_AndScheduledForItsPerspectiveAsync() {
    var instanceId = Guid.CreateVersion7();
    var streamId = (Guid)TrackedGuid.New();
    var messageId = (Guid)TrackedGuid.New();
    await using var provider = _createServices();
    await using var connection = await _openAsync();
    await _registerInstanceAsync(connection, instanceId);
    var eventType = await _associatePerspectiveAsync(connection);

    var deadLetterId = await _importAsync(provider, messageId, streamId);
    await _recoverAsync(provider, deadLetterId);
    await _claimWorkAsync(connection, instanceId);

    var stored = await _storedStreamAsync(connection, messageId);
    await Assert.That(stored).IsEqualTo(streamId)
      .Because("a recovered event is appended to the receiving event store under the stream its producer wrote it to; "
        + $"the recovered inbox row read {await _inboxStateAsync(connection, messageId)}");
    await Assert.That(await _perspectiveWorkAsync(connection, messageId)).IsEquivalentTo([PERSPECTIVE])
      .Because($"the perspective that applies {eventType} is scheduled for the recovered event, as for one delivered normally");
  }

  [Test]
  public async Task ARecoveredEvent_RefusedByTheInboxCopy_LeavesItsDeadLetterNotRecovered_WithTheOutcomeRecordedAsync() {
    var instanceId = Guid.CreateVersion7();
    var streamId = (Guid)TrackedGuid.New();
    var messageId = (Guid)TrackedGuid.New();
    await using var provider = _createServices();
    await using var connection = await _openAsync();
    await _registerInstanceAsync(connection, instanceId);
    _ = await _associatePerspectiveAsync(connection);
    // The receiving stream already holds an ephemeral event, so the inbox copy refuses a Sourced one (WHIZ133).
    await _storeEphemeralEventAsync(connection, streamId);

    var deadLetterId = await _importAsync(provider, messageId, streamId);
    await _recoverAsync(provider, deadLetterId);
    await _claimWorkAsync(connection, instanceId);

    var (status, recoveredAt, notes) = await _deadLetterAsync(connection, deadLetterId);
    var refusal = await _refusalDeadLetterIdAsync(connection, messageId);
    await Assert.That(await _storedStreamAsync(connection, messageId)).IsNull()
      .Because("the copy refused the recovered event, so nothing was appended");
    await Assert.That(refusal).IsNotNull()
      .Because("the refused message is back in dead-letter custody, under the refusal");
    await Assert.That(status).IsNotEqualTo(RECOVERED)
      .Because("Recovered means the message was processed, and this one was refused");
    await Assert.That(status).IsEqualTo(HOLD_FOR_REVIEW);
    await Assert.That(recoveredAt).IsNull();
    await Assert.That(notes).Contains(refusal!.Value.ToString())
      .Because("the row records where custody went and why");
  }

  // ===== Helpers =====

  private ServiceProvider _createServices() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbang();
    services.AddScoped(_ => CreateDbContext());
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<WorkCoordinationDbContext>()
      .WithDriver.Postgres;
    // The receiving service's event catalog: the perspective it runs applies this event.
    services.AddSingleton<IEventTypeProvider>(new FixedEventTypeProvider(typeof(RecoveredOrderPlaced)));
    return services.BuildServiceProvider();
  }

  /// <summary>Gives the event dead-letter custody as the transport dead-letter drain does: the wire body verbatim, the envelope type name, no session.</summary>
  private async Task<Guid> _importAsync(ServiceProvider provider, Guid messageId, Guid streamId) {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var envelopeTypeInfo = jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var payload = new RecoveredOrderPlaced(streamId, "R-1");
    var wireBody = JsonSerializer.Serialize(new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(payload, jsonOptions),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.CreateVersion7(), ServiceName = "upstream-service", HostName = "upstream-host", ProcessId = 1,
        },
        // The producer's dispatcher writes the stream into the first hop under this key.
        Metadata = new Dictionary<string, JsonElement> { ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()) },
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-a", UserId = "SYSTEM" }),
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    }, envelopeTypeInfo);

    using var scope = provider.CreateScope();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions);
    var imported = await coordinator.ImportBrokerDeadLetterAsync(new BrokerDeadLetterImport(
      MessageId: messageId,
      StreamId: null,
      MessageType: EnvelopeTypeNameHelper.Format(TypeNameFormatter.AssemblyQualifiedName(typeof(RecoveredOrderPlaced))),
      Destination: "topic/subscription",
      EnvelopeJson: wireBody,
      BrokerReason: "PoisonQuarantine",
      BrokerDescription: "first enqueued past the age threshold",
      EnqueuedAt: new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
      DeliveryCount: 1));
    await Assert.That(imported).IsTrue();

    await using var connection = await _openAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT dead_letter_id FROM wh_dead_letters WHERE source_table = 'broker' AND source_id = @id";
    command.Parameters.AddWithValue("id", messageId);
    return (Guid)(await command.ExecuteScalarAsync())!;
  }

  /// <summary>Recovers the row through the recovery service the Postgres driver registers, as the recovery worker does.</summary>
  private static async Task _recoverAsync(ServiceProvider provider, Guid deadLetterId) {
    using var scope = provider.CreateScope();
    var recovery = scope.ServiceProvider.GetRequiredService<IDeadLetterRecoveryService>();
    await Assert.That(await recovery.RecoverAsync(deadLetterId)).IsTrue();
  }

  private static async Task<string> _associatePerspectiveAsync(NpgsqlConnection connection) {
    var payloadType = EventTypeMatchingHelper.ExtractInnerPayloadTypeName(
      EnvelopeTypeNameHelper.Format(TypeNameFormatter.AssemblyQualifiedName(typeof(RecoveredOrderPlaced))));
    await using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test', normalize_event_type(@t), NOW(), NOW())
      RETURNING normalized_message_type
      """;
    command.Parameters.AddWithValue("t", payloadType);
    command.Parameters.AddWithValue("p", PERSPECTIVE);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  private static async Task _storeEphemeralEventAsync(NpgsqlConnection connection, Guid streamId) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at, flags)
      VALUES (@id, @s, @s, 'Upstream', 'Upstream.CursorMoved', 1, NOW(), @flags)
      """;
    // An id below the recovered message's, so the stream's kind is decided before it arrives.
    command.Parameters.AddWithValue("id", Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    command.Parameters.AddWithValue("s", streamId);
    command.Parameters.AddWithValue("flags", EPHEMERAL_FLAG);
    await command.ExecuteNonQueryAsync();
  }

  private static async Task _claimWorkAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT * FROM claim_work(@inst, 'test-svc', 'test-host', 100, 100, 100, 100)";
    command.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // drain
    }
  }

  private static async Task<Guid?> _storedStreamAsync(NpgsqlConnection connection, Guid messageId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT stream_id FROM wh_event_store WHERE event_id = @id";
    command.Parameters.AddWithValue("id", messageId);
    return await command.ExecuteScalarAsync() as Guid?;
  }

  private static async Task<List<string>> _perspectiveWorkAsync(NpgsqlConnection connection, Guid messageId) {
    var names = new List<string>();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT perspective_name FROM wh_perspective_events WHERE event_id = @id ORDER BY perspective_name";
    command.Parameters.AddWithValue("id", messageId);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names.Add(reader.GetString(0));
    }
    return names;
  }

  private static async Task<string> _inboxStateAsync(NpgsqlConnection connection, Guid messageId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT format('is_event=%s stream_id=%s', is_event, stream_id) FROM wh_inbox_state WHERE message_id = @id";
    command.Parameters.AddWithValue("id", messageId);
    return await command.ExecuteScalarAsync() as string ?? "no row";
  }

  private static async Task<(int Status, DateTimeOffset? RecoveredAt, string Notes)> _deadLetterAsync(
      NpgsqlConnection connection, Guid deadLetterId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT recovery_status, recovered_at, COALESCE(operator_notes, '') FROM wh_dead_letters WHERE dead_letter_id = @id";
    command.Parameters.AddWithValue("id", deadLetterId);
    await using var reader = await command.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    return (reader.GetInt32(0),
      await reader.IsDBNullAsync(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
      reader.GetString(2));
  }

  private static async Task<Guid?> _refusalDeadLetterIdAsync(NpgsqlConnection connection, Guid messageId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT dead_letter_id FROM wh_dead_letters WHERE source_table = 'wh_inbox' AND source_id = @id";
    command.Parameters.AddWithValue("id", messageId);
    return await command.ExecuteScalarAsync() as Guid?;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    command.Parameters.AddWithValue("id", instanceId);
    await command.ExecuteNonQueryAsync();
  }

  private sealed class FixedEventTypeProvider(params Type[] eventTypes) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }
}
