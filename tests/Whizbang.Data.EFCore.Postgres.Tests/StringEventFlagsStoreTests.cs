// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1612: the store functions read an event's flags from the stored message's JSON. A host whose JSON options
/// include <see cref="JsonStringEnumConverter"/> wrote them as names ("Collective, Ephemeral"), and the store kept
/// only <c>Collective</c>: an ephemeral event was stored as Sourced, an at-most-once one was owed to every
/// consumer, and nothing reported it. Every bit must reach <c>wh_outbox.flags</c>, <c>wh_inbox.flags</c> and
/// <c>wh_event_store.flags</c> whichever form the JSON carries.
/// </summary>
/// <remarks>
/// The round trips take their values from <see cref="EventFlags"/> itself, so a bit added to the enum is
/// tested here the day it is added, and the store's name mapping fails this suite until it knows the new name.
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#treatment-flags</docs>
[Category("Shard5")]
public class StringEventFlagsStoreTests : EFCoreTestBase {
  private const string TOPIC = "string-flags-topic";
  private const string EVENT_TYPE = "Probe.Contracts.FlagsProbed, Probe.Contracts";

  /// <summary>
  /// Every value <see cref="EventFlags"/> defines, every bit at once, and two mixed combinations.
  /// </summary>
  public static IEnumerable<EventFlags> EveryEventFlagsValue() {
    var every = EventFlags.None;
    foreach (var value in Enum.GetValues<EventFlags>()) {
      every |= value;
      yield return value;
    }
    yield return every;
    yield return EventFlags.Collective | EventFlags.Ephemeral;
    yield return EventFlags.Composite | EventFlags.NoRebroadcast | EventFlags.AtMostOnce;
  }

  [Test]
  [MethodDataSource(nameof(EveryEventFlagsValue))]
  public async Task StoreOutbox_FlagsWrittenAsNames_KeepsEveryBitAsync(EventFlags flags) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var message = _outbox(streamId, flags);

    await _storeOutboxAsync(conn, _withNamedFlags([message], flags, policy: null));

    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_outbox WHERE message_id = @id", message.MessageId))
      .IsEqualTo((int)flags)
      .Because($"the outbox row must carry every bit of '{_names(flags, null)}', not just Collective");
    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_event_store WHERE event_id = @id", message.MessageId))
      .IsEqualTo((int)flags)
      .Because("the event store reads the event's kind (ephemeral, at-most-once, compacted) from these bits");
  }

  [Test]
  [MethodDataSource(nameof(EveryEventFlagsValue))]
  public async Task StoreInbox_FlagsWrittenAsNames_KeepsEveryBitAsync(EventFlags flags) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var message = _inbox(streamId, flags);

    await _storeInboxAsync(conn, _withNamedFlags([message], flags, policy: null));

    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_inbox WHERE message_id = @id", message.MessageId))
      .IsEqualTo((int)flags)
      .Because($"the inbox row must carry every bit of '{_names(flags, null)}', not just Collective");
    await _emitInboxAsync(conn, message.MessageId);
    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_event_store WHERE event_id = @id", message.MessageId))
      .IsEqualTo((int)flags);
  }

  [Test]
  [Arguments("camelCase")]
  [Arguments("snake_case_lower")]
  [Arguments("SNAKE_CASE_UPPER")]
  [Arguments("kebab-case-lower")]
  public async Task StoreOutbox_FlagsNamedUnderANamingPolicy_KeepsEveryBitAsync(string policyName) {
    var flags = _every();
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var message = _outbox((Guid)TrackedGuid.New(), flags);

    await _storeOutboxAsync(conn, _withNamedFlags([message], flags, _policy(policyName)));

    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_outbox WHERE message_id = @id", message.MessageId))
      .IsEqualTo((int)flags)
      .Because($"'{_names(flags, _policy(policyName))}' names the same bits a converter with no policy would write");
  }

  [Test]
  public async Task StoreInbox_FlagsNamedUnderANamingPolicy_KeepsEveryBitAsync() {
    var flags = _every();
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var message = _inbox((Guid)TrackedGuid.New(), flags);

    await _storeInboxAsync(conn, _withNamedFlags([message], flags, JsonNamingPolicy.SnakeCaseLower));

    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_inbox WHERE message_id = @id", message.MessageId))
      .IsEqualTo((int)flags);
  }

  [Test]
  public async Task StoreOutbox_FlagsWrittenAsNamesAndANumber_KeepsEveryBitAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    const EventFlags flags = EventFlags.Ephemeral | EventFlags.AtMostOnce;
    var message = _outbox((Guid)TrackedGuid.New(), flags);

    await _storeOutboxAsync(conn, _withFlagsText([message], "Ephemeral, 32"));

    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_outbox WHERE message_id = @id", message.MessageId))
      .IsEqualTo((int)flags)
      .Because("a bit with no name is written as its number; it is still one of the event's bits");
  }

  [Test]
  public async Task StoreOutbox_FlagsNameNoEventFlagsValueHas_RaisesAndStoresNothingAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var message = _outbox((Guid)TrackedGuid.New(), EventFlags.Ephemeral);
    var json = _withFlagsText([message], "Ephemeral, Hibernating");

    var thrown = await Assert.That(async () => await _storeOutboxAsync(conn, json)).Throws<PostgresException>();

    await Assert.That(thrown!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidTextRepresentation);
    await Assert.That(thrown.MessageText).Contains("Hibernating")
      .Because("an event whose kind cannot be read must not be stored as Sourced without a word");
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_outbox WHERE message_id = @id", message.MessageId))
      .IsEqualTo(0L);
  }

  [Test]
  public async Task StoreInbox_FlagsNameNoEventFlagsValueHas_RaisesAndStoresNothingAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var message = _inbox((Guid)TrackedGuid.New(), EventFlags.Ephemeral);
    var json = _withFlagsText([message], "Hibernating");

    var thrown = await Assert.That(async () => await _storeInboxAsync(conn, json)).Throws<PostgresException>();

    await Assert.That(thrown!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidTextRepresentation);
    await Assert.That(thrown.MessageText).Contains("Hibernating");
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_inbox WHERE message_id = @id", message.MessageId))
      .IsEqualTo(0L);
  }

  // ---------------------------------------------------------------------------------------------
  // End to end: a coordinator whose options carry JsonStringEnumConverter stores every bit.
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task Coordinator_OptionsWithAStringEnumConverter_StoresEveryOutboxBitAsync() {
    var flags = _every();
    await using var dbContext = CreateDbContext();
    var message = _outbox((Guid)TrackedGuid.New(), flags);

    await _coordinatorWithStringEnums(dbContext).StoreOutboxMessagesAsync([message], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_outbox WHERE message_id = @id", message.MessageId))
      .IsEqualTo((int)flags);
    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_event_store WHERE event_id = @id", message.MessageId))
      .IsEqualTo((int)flags);
  }

  [Test]
  public async Task Coordinator_OptionsWithAStringEnumConverter_StoresEveryInboxBitAsync() {
    var flags = _every();
    await using var dbContext = CreateDbContext();
    var message = _inbox((Guid)TrackedGuid.New(), flags);

    await _coordinatorWithStringEnums(dbContext).StoreInboxMessagesAsync([message], partitionCount: 100);

    var conn = await _openAsync(dbContext);
    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_inbox WHERE message_id = @id", message.MessageId))
      .IsEqualTo((int)flags);
    await _emitInboxAsync(conn, message.MessageId);
    await Assert.That(await _intAsync(conn, "SELECT flags FROM wh_event_store WHERE event_id = @id", message.MessageId))
      .IsEqualTo((int)flags);
  }

  // ---------------------------------------------------------------------------------------------

  private static EventFlags _every() =>
    Enum.GetValues<EventFlags>().Aggregate(EventFlags.None, (all, value) => all | value);

  private static JsonNamingPolicy _policy(string name) => name switch {
    "camelCase" => JsonNamingPolicy.CamelCase,
    "snake_case_lower" => JsonNamingPolicy.SnakeCaseLower,
    "SNAKE_CASE_UPPER" => JsonNamingPolicy.SnakeCaseUpper,
    "kebab-case-lower" => JsonNamingPolicy.KebabCaseLower,
    _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such naming policy in this test"),
  };

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinatorWithStringEnums(WorkCoordinationDbContext dbContext) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    options.Converters.Add(new JsonStringEnumConverter());
    return new(dbContext, options);
  }

  // The form JsonStringEnumConverter writes for these flags: a comma-separated name list.
  private static string _names(EventFlags flags, JsonNamingPolicy? policy) {
    var options = new JsonSerializerOptions();
    var converter = (JsonConverter<EventFlags>)new JsonStringEnumConverter<EventFlags>(policy, allowIntegerValues: false)
      .CreateConverter(typeof(EventFlags), options);
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      converter.Write(writer, flags, options);
    }
    using var document = JsonDocument.Parse(buffer.ToArray());
    return document.RootElement.ValueKind == JsonValueKind.String
      ? document.RootElement.GetString()!
      : throw new InvalidOperationException($"the converter wrote {document.RootElement.ValueKind}, not names; the probe proves nothing");
  }

  // Serializes the messages as the coordinator does, then rewrites each Flags field as the names.
  private static string _withNamedFlags<T>(T[] messages, EventFlags flags, JsonNamingPolicy? policy) =>
    _withFlagsText(messages, _names(flags, policy));

  private static string _withFlagsText<T>(T[] messages, string flagsText) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var array = JsonNode.Parse(JsonSerializer.Serialize(messages, options.GetTypeInfo(typeof(T[]))))!.AsArray();
    foreach (var element in array) {
      element!["Flags"] = JsonValue.Create(flagsText);
    }
    return array.ToJsonString();
  }

  private static OutboxMessage _outbox(Guid streamId, EventFlags flags) =>
    CreateTestOutboxMessage((Guid)TrackedGuid.New(), TOPIC, streamId, isEvent: true) with {
      MessageType = EVENT_TYPE,
      Flags = flags,
    };

  private static InboxMessage _inbox(Guid streamId, EventFlags flags) {
    var messageId = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(new { probe = true }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "ProbeHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = EVENT_TYPE,
      StreamId = streamId,
      IsEvent = true,
      Flags = flags,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
    };
  }

  private static async Task _storeOutboxAsync(NpgsqlConnection conn, string json) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM store_outbox_messages(@messages::jsonb, NULL::uuid, NULL::timestamptz, NOW(), 10000)";
    cmd.Parameters.AddWithValue("messages", json);
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task _storeInboxAsync(NpgsqlConnection conn, string json) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM store_inbox_messages(@messages::jsonb, NULL::uuid, NULL::timestamptz, NOW(), 100)";
    cmd.Parameters.AddWithValue("messages", json);
    _ = await cmd.ExecuteScalarAsync();
  }

  // Leases the stored row to one instance and runs the claim's emit chain over it, as claim_work does.
  private static async Task _emitInboxAsync(NpgsqlConnection conn, Guid messageId) {
    var instanceId = (Guid)TrackedGuid.New();
    await using (var lease = conn.CreateCommand()) {
      lease.CommandText = "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @id";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("id", messageId);
      if (await lease.ExecuteNonQueryAsync() != 1) {
        throw new InvalidOperationException("the received row was not stored in the inbox; the probe proves nothing");
      }
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
    emit.Parameters.AddWithValue("inst", instanceId);
    _ = await emit.ExecuteScalarAsync();
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<int?> _intAsync(NpgsqlConnection conn, string sql, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(id), id);
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string sql, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(id), id);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }
}
