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
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The append-to-archived policy (#1738, WS-3 S9) on the Dapper driver: its event store and its coordinator surface the
/// store's archived-stream refusal as <see cref="StreamArchivedException"/>, its coordinator stores the configured
/// policies, and the driver registers the maintenance step that performs queued restores. The SQL itself is covered by
/// the EF Core <c>ArchiveAppendPolicySqlTests</c> and <c>ArchiveRestoreRequestSqlTests</c>; these lock this driver's side.
/// </summary>
/// <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
public class DapperArchiveAppendPolicyTests : PostgresTestBase {
  [Test]
  public async Task AppendAsync_ToAnArchivedStream_Refuse_ThrowsStreamArchivedExceptionAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });
    _ = await _archiveStore().WriteAsync(ArchiveBatch.WholeStream(stream));

    var thrown = await Assert.That(async () => await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream }))
      .Throws<StreamArchivedException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(stream);
    await Assert.That(thrown.ArchivedThroughVersion).IsEqualTo(0L);
    await Assert.That(thrown.Policy).IsEqualTo(AppendToArchivedPolicy.Refuse);
    await Assert.That(thrown.InnerException).IsTypeOf<PostgresException>();
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_AnAwaitedWriteToAnArchivedStream_ThrowsStreamArchivedExceptionAsync() {
    var coordinator = _coordinator();
    var stream = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream)], partitionCount: 10000);
    _ = await _archiveStore().WriteAsync(ArchiveBatch.WholeStream(stream));

    var thrown = await Assert.That(async () =>
        await coordinator.StoreOutboxMessagesAsync([_outbox(stream) with { RaiseOnMixedStream = true }], partitionCount: 10000))
      .Throws<StreamArchivedException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(stream);
    await Assert.That(thrown.Policy).IsEqualTo(AppendToArchivedPolicy.Refuse);
  }

  [Test]
  public async Task SyncStreamTypePolicies_ThroughTheCoordinator_StoresTheConfiguredSetAsync() {
    var coordinator = _coordinator();

    await ((IWorkCoordinator)coordinator).SyncStreamTypePoliciesAsync([
      new StreamTypePolicy("Shop.Order", AppendToArchivedPolicy.AutoRestore),
      new StreamTypePolicy("Shop.Cart", AppendToArchivedPolicy.Refuse),
    ]);
    await ((IWorkCoordinator)coordinator).SyncStreamTypePoliciesAsync([new StreamTypePolicy("Shop.Order", AppendToArchivedPolicy.AutoRestore)]);

    await using var conn = await _openAsync();
    var rows = (await conn.QueryAsync<string>(
      "SELECT stream_type || '|' || append_to_archived FROM wh_stream_type_policies ORDER BY 1")).ToList();
    await Assert.That(rows).IsEquivalentTo(["Shop.Order|AutoRestore"])
      .Because("each sync replaces the stored set whole, so a type configuration no longer names refuses again");
  }

  [Test]
  public async Task AddWhizbangPostgres_RegistersTheRestoreRequestStep_AsAMaintenanceStepAsync() {
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();

    var steps = provider.GetServices<IMaintenanceStep>().ToList();

    await Assert.That(steps.OfType<ArchiveRestoreRequestStep>()).Count().IsEqualTo(1)
      .Because("queued restores are performed by the maintenance cycle, under the maintainer duty, on either driver");
  }

  // ===== Helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private PostgresTableArchiveStore _archiveStore() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private DapperWorkCoordinator _coordinator() =>
    new(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), NullLogger<DapperWorkCoordinator>.Instance);

  private DapperPostgresEventStore _eventStore() {
    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      new EventEnvelopeJsonbAdapter(jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null,
      NullLogger<DapperPostgresEventStore>.Instance);
  }

  private static OutboxMessage _outbox(Guid streamId) {
    var msgId = (Guid)TrackedGuid.New();
    return new OutboxMessage {
      MessageId = msgId,
      Destination = "archive-topic",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(msgId), JsonElement.Parse("{\"k\":1}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Probe.Contracts.Deposited, Probe.Contracts",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = true,
    };
  }
}
