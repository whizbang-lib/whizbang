// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1885 on real PostgreSQL: inbox messages the inbox batcher accepted and could not store are written to
/// wh_dead_letters through <c>wh_store_inbox_dead_letters</c> as wh_inbox dead letters, in the shape dead-letter
/// recovery reads to re-enter a message through the inbox store, and each is logged in wh_log.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/257_UnstoredInboxMessagesAreDeadLettered.sql</code-under-test>
[Category("Shard5")]
public class InboxFlushDeadLetterTests : EFCoreTestBase {
  private const int PARTITION_COUNT = 10000;

  /// <summary>
  /// Messages that were never stored are written as wh_inbox dead letters with the request's audit columns, each is
  /// logged in wh_log under its message id, and the dead letter reads back as the envelope the message was accepted
  /// with, which the inbox store then stores as a recovered message.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task StoreInboxDeadLetters_WritesARecoverableDeadLetterAndALogRowPerMessageAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    var first = _inbox((Guid)TrackedGuid.New(), stream);
    var second = _inbox((Guid)TrackedGuid.New(), stream);
    var instanceId = (Guid)TrackedGuid.New();

    var kept = await coordinator.StoreInboxDeadLettersAsync([first, second],
      new InboxDeadLetterRequest(MessageFailureReason.InboxStoreFailed, "pool exhausted", 5, instanceId, "gen-1"),
      cancellationToken);

    await Assert.That(kept).IsTrue();
    List<Guid> ids = [first.MessageId, second.MessageId];
    await Assert.That(await _countAsync(
      "SELECT count(*) FROM wh_dead_letters WHERE source_table = 'wh_inbox' AND source_id = ANY(@ids) AND failure_reason = 24 "
      + "AND attempts_when_dlq = 5 AND generation = 'gen-1' AND error_text = 'pool exhausted' AND stream_id IS NOT NULL",
      ids, cancellationToken)).IsEqualTo(2L);
    await Assert.That(await _countAsync(
      "SELECT count(*) FROM wh_log WHERE source = 'inbox_flush' AND log_level = 3 AND message_id = ANY(@ids)",
      ids, cancellationToken)).IsEqualTo(2L)
      .Because("each message that was not stored is logged in wh_log under its id");

    var deadLetterId = await _scalarAsync<Guid>(
      "SELECT dead_letter_id FROM wh_dead_letters WHERE source_id = (@ids)[1]", ids, cancellationToken);
    await using var recoveryContext = CreateDbContext();
    var redelivery = await new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(recoveryContext)
      .ReadRedeliveryAsync(deadLetterId, cancellationToken);
    await Assert.That(redelivery).IsNotNull()
      .Because("recovery re-enters an inbox dead letter from the envelope it keeps");
    var envelope = (MessageEnvelope<JsonElement>?)JsonSerializer.Deserialize(
      redelivery!.EnvelopeJson, JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    await Assert.That(envelope!.MessageId.Value).IsEqualTo(first.MessageId)
      .Because("the dead letter keeps the very envelope the batcher accepted");
    await Assert.That(redelivery.MessageType).IsEqualTo(first.MessageType);

    await coordinator.StoreInboxMessagesAsync([first with { RecoveredFromDeadLetterId = deadLetterId }], PARTITION_COUNT, cancellationToken);
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_inbox WHERE message_id = ANY(@ids)", ids, cancellationToken))
      .IsEqualTo(1L).Because("the recovered message is stored through the inbox store");
  }

  /// <summary>
  /// A message the store already committed (only its reply was lost) is not dead-lettered, a message already holding
  /// an unrecovered dead letter is not dead-lettered twice, and an empty batch writes nothing.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task StoreInboxDeadLetters_SkipsAMessageAlreadyStoredOrAlreadyDeadLetteredAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    var stored = _inbox((Guid)TrackedGuid.New(), stream);
    var unstored = _inbox((Guid)TrackedGuid.New(), stream);
    var request = new InboxDeadLetterRequest(MessageFailureReason.InboxStoreFailed, null, 1, Guid.Empty, "gen-1");
    await coordinator.StoreInboxMessagesAsync([stored], PARTITION_COUNT, cancellationToken);

    await Assert.That(await coordinator.StoreInboxDeadLettersAsync([], request, cancellationToken)).IsTrue();
    await coordinator.StoreInboxDeadLettersAsync([stored, unstored], request, cancellationToken);
    await coordinator.StoreInboxDeadLettersAsync([unstored], request, cancellationToken);

    await Assert.That(await _countAsync("SELECT count(*) FROM wh_dead_letters WHERE source_id = ANY(@ids)", [stored.MessageId], cancellationToken))
      .IsEqualTo(0L).Because("the store committed this message; dead-lettering it would store it twice once recovered");
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_dead_letters WHERE source_id = ANY(@ids) AND error_text IS NULL AND error_fingerprint IS NULL", [unstored.MessageId], cancellationToken))
      .IsEqualTo(1L).Because("the same message reported twice keeps one dead letter");
  }

  private static InboxMessage _inbox(Guid messageId, Guid streamId) => new() {
    MessageId = messageId,
    HandlerName = "InboxBatch",
    Envelope = CreateTestEnvelope(messageId),
    EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
    MessageType = "TestMessage, TestAssembly",
    StreamId = streamId,
    Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
  };

  private async Task<long> _countAsync(string sql, List<Guid> ids, CancellationToken cancellationToken) =>
    await _scalarAsync<long>(sql, ids, cancellationToken);

  private async Task<T> _scalarAsync<T>(string sql, List<Guid> ids, CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(ids), ids.ToArray());
    return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
  }
}
