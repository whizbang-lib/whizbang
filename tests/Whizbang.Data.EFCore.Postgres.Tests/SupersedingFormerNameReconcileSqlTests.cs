// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2269 (owner decision 2026-11-13): an event under a superseding type's current name supersedes one with the same key
/// stored under any of the type's former names. They are one logical type, so the superseding history reconcile walks the
/// type's former names too, in their stored form (<see cref="TypeNameFormatter.FormatFormerName"/>: a former name from
/// another assembly keeps that assembly, a bare one takes the type's own), reads each event's key with the type's own
/// accessor, and judges supersession across all of the type's names. Events are written through the event store's append
/// path; a former name is simulated by relabeling them as an older build would have stored them.
/// </summary>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/SupersedingHistoryReconcileStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class SupersedingFormerNameReconcileSqlTests : EFCoreTestBase {
  private static readonly Type _type = typeof(SupersedingProbeEmbedded);
  private static readonly string _formerQualified = $"{TypeNameFormatter.FormatClrTypeName(_type)}, Former.Contracts";
  private const string FORMER_BARE = "Whizbang.Data.EFCore.Postgres.Tests.SupersedingProbeEmbeddedFormer";

  [Test]
  public async Task Reconcile_AnOlderEventUnderAFormerAssemblyQualifiedName_IsSupersededByTheNewerOneUnderTheCurrentNameAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var old = await _storeAsync(conn, stream, "model-a", relabelTo: _formerQualified);
    var other = await _storeAsync(conn, stream, "model-b", relabelTo: _formerQualified);
    var current = await _storeAsync(conn, stream, "model-a");

    await _step().ReconcileAsync(_services(_formerQualified), CancellationToken.None);

    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([old])
      .Because("the newer event of the same key under the current name supersedes the one the older build stored");
    await Assert.That(await _textAsync(conn, "SELECT supersede_key FROM wh_event_store WHERE event_id = @id", old)).IsEqualTo("model-a")
      .Because("the key of an event under a former name is read with the type's own accessor");
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([old, other, current])
      .Because("the reconcile deletes nothing; the reaper's gates stay the only path to deletion");
  }

  [Test]
  public async Task Reconcile_AnEventUnderABareFormerName_IsWalkedUnderTheTypesOwnAssembly_AndSupersededAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var formerStored = TypeNameFormatter.FormatFormerName(_type, FORMER_BARE);
    var old = await _storeAsync(conn, stream, "model-a", relabelTo: formerStored);
    _ = await _storeAsync(conn, stream, "model-a");

    await _step().ReconcileAsync(_services(FORMER_BARE), CancellationToken.None);

    await Assert.That(await _textsAsync(conn,
        "SELECT event_type FROM wh_type_history_walks WHERE operation = 'superseding' AND events_matched > 0"))
      .Contains(formerStored).Because("a bare former name is stored under the type's own assembly, and walked there");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([old]);
  }

  [Test]
  public async Task Reconcile_ANewerEventUnderTheCurrentName_LeavesAnotherKeyUnderAFormerNameAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    _ = await _storeAsync(conn, stream, "model-b", relabelTo: _formerQualified);
    _ = await _storeAsync(conn, stream, "model-a");

    await _step().ReconcileAsync(_services(_formerQualified), CancellationToken.None);

    await Assert.That(await _queuedAsync(conn)).IsEmpty()
      .Because("supersession across names is by key: a different key is a different fact");
  }

  [Test]
  public async Task Reconcile_WithoutTheCatalog_WalksOnlyTheCurrentNameAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    _ = await _storeAsync(conn, stream, "model-a", relabelTo: _formerQualified);
    _ = await _storeAsync(conn, stream, "model-a");

    await _step().ReconcileAsync(_services(formerName: null), CancellationToken.None);

    await Assert.That(await _queuedAsync(conn)).IsEmpty()
      .Because("the control: a host with no message type catalog knows no former name, as before");
  }

  // ===== Helpers =====

  private static SupersedingHistoryReconcileStep _step() => new(NullLogger.Instance, 100, 10, () => [_type]);

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private ServiceProvider _services(string? formerName) {
    var services = new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"));
    if (formerName is not null) {
      services.AddSingleton<IMessageTypeCatalog>(new Catalog(
        new MessageTypeCatalogEntry(_type, TypeNameFormatter.FormatClrTypeName(_type), "event", null) { FormerNames = [formerName] }));
    }
    return services.BuildServiceProvider();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>
  /// An event stored through the event store's append path and numbered by the commit-order stamper; relabeled, when
  /// asked, to the name an older build stored the type under.
  /// </summary>
  private async Task<Guid> _storeAsync(NpgsqlConnection conn, Guid stream, string model, string? relabelTo = null) {
    var envelope = new MessageEnvelope<SupersedingProbeEmbedded> {
      MessageId = MessageId.New(),
      Payload = new SupersedingProbeEmbedded { DocumentId = stream, Model = model },
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    await using (var context = CreateDbContext()) {
      await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(stream, envelope);
    }
    var id = envelope.MessageId.Value;
    if (relabelTo is not null) {
      await _executeAsync(conn, "UPDATE wh_event_store SET event_type = @t WHERE event_id = @id", ("t", relabelTo), ("id", id));
    }
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    // The write path queued nothing across names; only the reconcile's queue is under test.
    await _executeAsync(conn, "DELETE FROM wh_superseded_events");
    return id;
  }

  private static async Task<List<Guid>> _queuedAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_superseded_events ORDER BY event_id";
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<List<Guid>> _storedAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<string?> _textAsync(NpgsqlConnection conn, string sql, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("id", id);
    return await cmd.ExecuteScalarAsync() as string;
  }

  private static async Task<List<string>> _textsAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var texts = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      texts.Add(reader.GetString(0));
    }
    return texts;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
