// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// On the Dapper driver, a background write to a stream archived whole under <see cref="AppendToArchivedPolicy.AutoRestore"/>
/// is redriven once its restore completes (#1934). The write is dead-lettered with
/// <see cref="MessageFailureReason.AwaitingRestore"/> and the stream's restore is queued; the maintenance step restores
/// the stream and makes the dead letter due; the dead-letter recovery service the driver registers fetches it and puts
/// the write back through the outbox, as the EF Core driver's does.
/// </summary>
/// <docs>fundamentals/events/archive-store#restore-requests</docs>
/// <docs>operations/dead-letter-queue/recovery</docs>
public class DapperArchiveRestoreRedriveTests : PostgresTestBase {
  private const string MESSAGE_TYPE = "Probe.Restore.Deposited, Probe.Restore";
  private const string STREAM_TYPE = "Probe.Restore.Deposited";

  [Test]
  public async Task AddWhizbangPostgres_AnAwaitingRestoreDeadLetter_IsRedrivenThroughTheOutbox_OnceItsRestoreCompletesAsync() {
    var coordinator = _coordinator();
    await ((IWorkCoordinator)coordinator).SyncStreamTypePoliciesAsync([new StreamTypePolicy(STREAM_TYPE, AppendToArchivedPolicy.AutoRestore)]);
    var stream = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream, (Guid)TrackedGuid.New())], partitionCount: 10000);
    await Assert.That((await _archiveStore().WriteAsync(ArchiveBatch.WholeStream(stream))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    var write = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream, write)], partitionCount: 10000);
    var deadLetter = await _awaitingRestoreDeadLetterAsync(write);
    await Assert.That(deadLetter.HasValue).IsTrue()
      .Because("the control: a background write to the archived stream waits for its restore as a dead letter");

    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();
    await using (var maintenance = provider.CreateAsyncScope()) {
      var step = maintenance.ServiceProvider.GetServices<IMaintenanceStep>().OfType<ArchiveRestoreRequestStep>().Single();
      await step.RunAsync(maintenance.ServiceProvider, CancellationToken.None);
    }
    await using var scope = provider.CreateAsyncScope();
    var recovery = scope.ServiceProvider.GetService<IDeadLetterRecoveryService>();

    await Assert.That(recovery).IsNotNull()
      .Because("the dead-letter recovery worker redrives through this service, and the driver registers none");
    var due = await recovery!.FetchDueAsync(100);
    await Assert.That(due.Select(e => (e.DeadLetterId, e.FailureReason)))
      .Contains((deadLetter!.Value, MessageFailureReason.AwaitingRestore))
      .Because("the restore made the stream's awaiting dead letter due");
    await Assert.That(await recovery.RecoverAsync(deadLetter.Value)).IsTrue();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_outbox WHERE message_id = @write", new { write }))
      .IsEqualTo(1L).Because("the write re-enters the outbox it was refused from, now that its stream is back");
    await Assert.That(await conn.ExecuteScalarAsync<int>(
        "SELECT recovery_status FROM wh_dead_letters WHERE dead_letter_id = @id", new { id = deadLetter.Value }))
      .IsEqualTo((int)DeadLetterRecoveryStatus.Recovering)
      .Because("the outbox store claims the dead letter as it stores the write; its completion settles it");
  }

  // ===== Helpers =====

  private async Task<Guid?> _awaitingRestoreDeadLetterAsync(Guid write) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return await conn.ExecuteScalarAsync<Guid?>(
      "SELECT dead_letter_id FROM wh_dead_letters WHERE source_id = @write AND failure_reason = @reason",
      new { write, reason = (int)MessageFailureReason.AwaitingRestore });
  }

  private PostgresTableArchiveStore _archiveStore() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private DapperWorkCoordinator _coordinator() =>
    new(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), NullLogger<DapperWorkCoordinator>.Instance);

  private static OutboxMessage _outbox(Guid streamId, Guid messageId) => new() {
    MessageId = messageId,
    Destination = "restore-topic",
    Envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonElement.Parse("{\"k\":1}"), []),
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[" + MESSAGE_TYPE + "]], Whizbang.Core",
    MessageType = MESSAGE_TYPE,
    Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    StreamId = streamId,
    IsEvent = true,
  };
}
