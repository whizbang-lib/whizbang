// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Events.System;
using Whizbang.Core.Fingerprint;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2320: rewind events stored while they were Sourced are reclassified by the history walk and then reaped once the
/// system-event retention has passed since each was written, and not before. The retention reaches the reaper as the
/// type's grace, measured from the stored creation time, because a historical row carries no stamped expiry.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#framework-system-events</docs>
/// <code-under-test>src/Whizbang.Core/Events/System/SystemEventRetention.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class SystemEventRetentionSqlTests : EFCoreTestBase {
  [Test]
  [Timeout(120000)]
  public async Task HistoricalRewindEvents_AreReclassified_AndReapedOnlyOnceTheirRetentionHasPassedAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var old = await _storeRewindAsync(conn);
    var recent = await _storeRewindAsync(conn);
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = now() - interval '31 days' WHERE event_id = @id", ("id", old));
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_body WHERE event_id = ANY(@ids)", ("ids", new[] { old, recent })))
      .IsEqualTo(2L).Because("the control: both bodies are stored");

    var entries = new Whizbang.Core.Generated.GeneratedMessageTypeCatalog().GetAll()
      .Where(e => typeof(IEphemeralSystemEvent).IsAssignableFrom(e.Type)).ToArray();
    var catalog = new Catalog(entries);
    await using (var ctx = CreateDbContext()) {
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
      await new TypeDefinitionReconciler(new SingleCoordinatorScopeFactory(coordinator),
        Options.Create(new EphemeralOptions()), NullLogger<TypeDefinitionReconciler>.Instance, catalog).ReconcileAsync(cancellationToken);
    }
    var reclassified = await new EphemeralHistoryReclassifyStep(NullLogger.Instance, 100, 10)
      .ReclassifyAsync(_services(catalog), cancellationToken);

    await Assert.That(reclassified.EventsReclassified).IsEqualTo(2L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_store WHERE event_id = ANY(@ids) AND (flags & 8) = 8",
      ("ids", new[] { old, recent }))).IsEqualTo(2L);
    await Assert.That(await _scalarAsync(conn, "SELECT grace_seconds FROM wh_ephemeral_type_grace WHERE event_type = @t",
      ("t", TypeNameFormatter.Format(typeof(PerspectiveRewindStarted))))).IsEqualTo((long)SystemEventRetention.DEFAULT_TTL_SECONDS);

    await using (var ctx = CreateDbContext()) {
      IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
      await coordinator.PerformMaintenanceAsync(null, cancellationToken);
    }

    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", old))).IsEqualTo(0L)
      .Because("written 31 days ago, past the 30-day retention");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", recent))).IsEqualTo(1L)
      .Because("a reclassified event carries no stamped expiry; without the grace it would go after the global five minutes");
  }

  private async Task<Guid> _storeRewindAsync(NpgsqlConnection conn) {
    var stream = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<PerspectiveRewindStarted> {
      MessageId = MessageId.New(),
      Payload = new PerspectiveRewindStarted(stream, Guid.Empty, "probe", Guid.Empty, null, false, DateTimeOffset.UnixEpoch),
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    await using (var context = CreateDbContext()) {
      await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(stream, envelope);
    }
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    return envelope.MessageId.Value;
  }

  private ServiceProvider _services(IMessageTypeCatalog catalog) =>
    new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton(Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = true }))
      .AddSingleton(catalog)
      .BuildServiceProvider();

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private sealed class SingleCoordinatorScopeFactory(IWorkCoordinator coordinator) : IServiceScopeFactory, IServiceScope, IServiceProvider {
    public IServiceScope CreateScope() => this;
    public IServiceProvider ServiceProvider => this;
    public object? GetService(Type serviceType) => serviceType == typeof(IWorkCoordinator) ? coordinator : null;
    public void Dispose() { }
  }
}
