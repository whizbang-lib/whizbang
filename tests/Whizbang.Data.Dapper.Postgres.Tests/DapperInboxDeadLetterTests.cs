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
/// #1885 through the Dapper coordinator: inbox messages the inbox batcher could not store are written to wh_dead_letters
/// through <c>wh_store_inbox_dead_letters</c> and logged in wh_log. The SQL behavior itself is covered by the EF Core
/// <c>InboxFlushDeadLetterTests</c>; these lock this coordinator's side of it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
public class DapperInboxDeadLetterTests : PostgresTestBase {
  private readonly JsonSerializerOptions _jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  [Test]
  public async Task StoreInboxDeadLetters_WritesARecoverableDeadLetterAndALogRowPerMessageAsync() {
    var coordinator = _build();
    var stream = (Guid)TrackedGuid.New();
    var message = _message(stream);

    var kept = await coordinator.StoreInboxDeadLettersAsync([message],
      new InboxDeadLetterRequest(MessageFailureReason.InboxStoreFailed, "pool exhausted", 3, (Guid)TrackedGuid.New(), "gen-1"));

    await Assert.That(kept).IsTrue();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var reason = await conn.ExecuteScalarAsync<int?>(
      "SELECT failure_reason FROM wh_dead_letters WHERE source_table = 'wh_inbox' AND source_id = @id AND attempts_when_dlq = 3",
      new { id = message.MessageId });
    await Assert.That(reason).IsEqualTo((int)MessageFailureReason.InboxStoreFailed);
    var logged = await conn.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_log WHERE source = 'inbox_flush' AND message_id = @id", new { id = message.MessageId });
    await Assert.That(logged).IsEqualTo(1L);
  }

  [Test]
  public async Task StoreInboxDeadLetters_EmptyBatch_WritesNothingAsync() {
    var kept = await _build().StoreInboxDeadLettersAsync([],
      new InboxDeadLetterRequest(MessageFailureReason.InboxStoreFailed, null, 1, Guid.Empty, "gen-1"));

    await Assert.That(kept).IsTrue();
  }

  private DapperWorkCoordinator _build() =>
    new(ConnectionString, _jsonOptions, NullLogger<DapperWorkCoordinator>.Instance);

  private static InboxMessage _message(Guid streamId) {
    var msgId = (Guid)TrackedGuid.New();
    return new InboxMessage {
      MessageId = msgId,
      HandlerName = "InboxBatch",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(msgId), JsonElement.Parse("{\"k\":1}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
    };
  }
}
