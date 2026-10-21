// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// WHIZ133 (#1546) through the Dapper coordinator: the store refuses an event that would make a stream hold both
/// ephemeral and Sourced events. A write whose caller awaits it fails with <see cref="MixedStreamEventException"/>;
/// any other write dead-letters only the refused row. The SQL behavior itself is covered by the EF Core
/// <c>MixedStreamGuardSqlTests</c>; these tests lock this coordinator's side of it.
/// </summary>
/// <docs>operations/diagnostics/whiz133</docs>
public class DapperMixedStreamGuardTests : PostgresTestBase {
  private const string EPHEMERAL_TYPE = "Probe.Contracts.UserIsTyping, Probe.Contracts";
  private const string SOURCED_TYPE = "Probe.Contracts.OrderPlaced, Probe.Contracts";

  private readonly JsonSerializerOptions _jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  [Test]
  public async Task StoreOutboxMessagesAsync_DirectCommitOfASourcedEventOnAnEphemeralStream_ThrowsMixedStreamEventExceptionAsync() {
    var coordinator = _build();
    var streamId = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_event(streamId, EPHEMERAL_TYPE, EventFlags.Ephemeral)], partitionCount: 10000);
    var sourced = _event(streamId, SOURCED_TYPE, EventFlags.None) with { RaiseOnMixedStream = true };

    var thrown = await Assert.That(async () =>
        await coordinator.StoreOutboxMessagesAsync([sourced], partitionCount: 10000))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(streamId);
    await Assert.That(thrown.EventId).IsEqualTo(sourced.MessageId);
    await Assert.That(thrown.Message).StartsWith(MixedStreamEventException.DIAGNOSTIC_ID);
    await Assert.That(thrown.InnerException).IsTypeOf<PostgresException>()
      .Because("the store's own error is kept for the stack trace and the SQL state");
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_BackgroundWriteOfASourcedEventOnAnEphemeralStream_DeadLettersTheRowAsync() {
    var coordinator = _build();
    var streamId = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_event(streamId, EPHEMERAL_TYPE, EventFlags.Ephemeral)], partitionCount: 10000);
    var sourced = _event(streamId, SOURCED_TYPE, EventFlags.None);

    await coordinator.StoreOutboxMessagesAsync([sourced], partitionCount: 10000);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var reason = await conn.ExecuteScalarAsync<int?>(
      "SELECT failure_reason FROM wh_dead_letters WHERE source_id = @id", new { id = sourced.MessageId });
    await Assert.That(reason).IsEqualTo((int)MessageFailureReason.MixedStreamRefused);
    var stored = await conn.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_event_store WHERE event_id = @id", new { id = sourced.MessageId });
    await Assert.That(stored).IsEqualTo(0L);
  }

  private DapperWorkCoordinator _build() =>
    new(ConnectionString, _jsonOptions, NullLogger<DapperWorkCoordinator>.Instance);

  private static OutboxMessage _event(Guid streamId, string messageType, EventFlags flags) {
    var msgId = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"k\":1}").RootElement,
      []);
    return new OutboxMessage {
      MessageId = msgId,
      Destination = "mixed-stream-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = messageType,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = true,
      Flags = flags,
    };
  }
}
