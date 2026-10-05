// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// The Dapper store's checked writes, against a real table.
/// </summary>
/// <remarks>
/// <para>
/// This store served the interface's defaults: it reported itself unchecked and wrote
/// unconditionally, so a per-stream apply computed from a stale read could overwrite a concurrent
/// collective write here while the identical apply was refused under Entity Framework. The runner's
/// retry covered one store and not the other.
/// </para>
/// <para>
/// Asserted against a real table because every guarantee here is PostgreSQL's: <c>xmin</c> moving on
/// update, an <c>UPDATE</c> matching no deleted row, <c>ON CONFLICT DO NOTHING</c> declining to
/// overwrite. None of that can be established against a fake.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperPostgresPerspectiveStore.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class DapperPerspectiveRowVersionTests : PostgresTestBase {

  private const string TABLE = "wh_per_dapper_rowversion_test";
  private JsonSerializerOptions _jsonOptions = null!;

  [Before(Test)]
  public async Task CreateTableAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    const string createSql = $"CREATE TABLE IF NOT EXISTS {TABLE} (" +
        "id UUID PRIMARY KEY, data JSONB NOT NULL, " +
        "metadata JSONB NOT NULL DEFAULT '{}'::jsonb, scope JSONB NOT NULL DEFAULT '{}'::jsonb, " +
        "assigned_to TEXT, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), " +
        "version INT NOT NULL DEFAULT 1)";
    await using (var cmd = new NpgsqlCommand(createSql, conn)) {
      await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    await using (var truncate = new NpgsqlCommand($"TRUNCATE {TABLE}", conn)) {
      await truncate.ExecuteNonQueryAsync(cancellationToken);
    }
    _jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        DapperPerspectiveTestJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
  }

  private DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel> _store() =>
    new(ConnectionString, TABLE, _jsonOptions);

  private static PerspectiveMetadata _metadata(long? commitSequence, string eventId = "e1") => new() {
    EventId = eventId,
    EventType = "TestEvent",
    CommitSequence = commitSequence,
  };

  private static DapperPostgresPerspectiveStoreTests.TestModel _model(string name) => new() { Name = name };

  private async Task<string?> _nameAsync(Guid streamId, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand($"SELECT data->>'Name' FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", streamId);
    return await cmd.ExecuteScalarAsync(cancellationToken) as string;
  }

  private async Task<string?> _assignedToAsync(Guid streamId, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand($"SELECT assigned_to FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", streamId);
    return await cmd.ExecuteScalarAsync(cancellationToken) as string;
  }

  private static Dictionary<string, object?> _physical(string assignedTo) =>
    new() { ["assigned_to"] = assignedTo };

  /// <summary>The read reports an absent row, then the row's version and the metadata beside it.</summary>
  [Test]
  [Timeout(120000)]
  public async Task ReadForApply_ReportsAbsence_ThenTheVersionAndMetadataAsync(CancellationToken cancellationToken) {
    var store = _store();
    var id = Guid.NewGuid();

    var before = await store.ReadForApplyAsync(id, cancellationToken);
    await Assert.That(before.Version.State).IsEqualTo(PerspectiveRowVersionState.Absent)
      .Because("a write for a row that is not there has to create it, and be refused if one appeared");
    await Assert.That(before.Metadata).IsNull();

    await store.UpsertAsync(id, _model("first"), new PerspectiveScope(), false, _metadata(7), cancellationToken);

    var after = await store.ReadForApplyAsync(id, cancellationToken);
    await Assert.That(after.Version.State).IsEqualTo(PerspectiveRowVersionState.Present)
      .Because("the store tracks a version now, rather than reporting itself unchecked");
    await Assert.That(after.Version.Value).IsNotEqualTo(0L);
    await Assert.That(after.Metadata!.CommitSequence).IsEqualTo(7L)
      .Because("the runner's idempotency filter reads this in the same statement as the version");
  }

  /// <summary>A write on the version the apply read lands.</summary>
  [Test]
  [Timeout(120000)]
  public async Task Upsert_OnTheCurrentVersion_LandsAsync(CancellationToken cancellationToken) {
    var store = _store();
    var id = Guid.NewGuid();
    await store.UpsertAsync(id, _model("first"), new PerspectiveScope(), false, _metadata(1), cancellationToken);

    var read = await store.ReadForApplyAsync(id, cancellationToken);
    await store.UpsertAsync(id, _model("second"), new PerspectiveScope(), false, _metadata(2), read.Version, cancellationToken);

    await Assert.That(await _nameAsync(id, cancellationToken)).IsEqualTo("second")
      .Because("nothing moved between the read and the write, so the write is the one that lands");
  }

  /// <summary>A write on a version something else has moved past is refused, and writes nothing.</summary>
  [Test]
  [Timeout(120000)]
  public async Task Upsert_OnAStaleVersion_IsRefused_AndWritesNothingAsync(CancellationToken cancellationToken) {
    var store = _store();
    var id = Guid.NewGuid();
    await store.UpsertAsync(id, _model("first"), new PerspectiveScope(), false, _metadata(1), cancellationToken);
    var stale = (await store.ReadForApplyAsync(id, cancellationToken)).Version;

    // Somebody else writes: xmin moves, and the version above is now history.
    await store.UpsertAsync(id, _model("collective"), new PerspectiveScope(), false, _metadata(2), cancellationToken);

    await Assert.That(async () =>
        await store.UpsertAsync(id, _model("stale"), new PerspectiveScope(), false, _metadata(3), stale, cancellationToken))
      .Throws<PerspectiveRowConflictException>()
      .Because("the apply computed its model from a row that has since moved, so the write must not land");
    await Assert.That(await _nameAsync(id, cancellationToken)).IsEqualTo("collective")
      .Because("a refused write writes nothing -- the other writer's row survives intact");
  }

  /// <summary>A write expecting no row is refused once a row exists.</summary>
  [Test]
  [Timeout(120000)]
  public async Task Upsert_ExpectingNoRow_WhenOneAppeared_IsRefusedAsync(CancellationToken cancellationToken) {
    var store = _store();
    var id = Guid.NewGuid();
    var absent = (await store.ReadForApplyAsync(id, cancellationToken)).Version;

    await store.UpsertAsync(id, _model("appeared"), new PerspectiveScope(), false, _metadata(1), cancellationToken);

    await Assert.That(async () =>
        await store.UpsertAsync(id, _model("mine"), new PerspectiveScope(), false, _metadata(2), absent, cancellationToken))
      .Throws<PerspectiveRowConflictException>()
      .Because("the apply decided to insert because there was no row, and that is no longer true");
    await Assert.That(await _nameAsync(id, cancellationToken)).IsEqualTo("appeared");
  }

  /// <summary>A write expecting a row that has since been deleted does not bring it back.</summary>
  [Test]
  [Timeout(120000)]
  public async Task Upsert_ExpectingARow_WhenItWasDeleted_DoesNotResurrectItAsync(CancellationToken cancellationToken) {
    var store = _store();
    var id = Guid.NewGuid();
    await store.UpsertAsync(id, _model("first"), new PerspectiveScope(), false, _metadata(1), cancellationToken);
    var version = (await store.ReadForApplyAsync(id, cancellationToken)).Version;

    await store.PurgeAsync(id, cancellationToken);

    await Assert.That(async () =>
        await store.UpsertAsync(id, _model("back"), new PerspectiveScope(), false, _metadata(2), version, cancellationToken))
      .Throws<PerspectiveRowConflictException>();
    await Assert.That(await _nameAsync(id, cancellationToken)).IsNull()
      .Because("the checked write is an UPDATE, which matches no deleted row -- an upsert would have "
             + "recreated a row somebody deleted on purpose");
  }

  /// <summary>
  /// A write the ordering guard refuses, on the version the apply read, is a quiet skip rather than a
  /// conflict.
  /// </summary>
  /// <remarks>
  /// The two refusals are indistinguishable at the statement -- both affect no row -- so the store
  /// re-reads the version to tell them apart. A stale event is ordinary and must not raise; a moved
  /// row is a conflict the runner retries. Getting this backwards would either hide conflicts or turn
  /// every late event into an exception.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task Upsert_OnTheCurrentVersion_RefusedByTheOrderingGuard_IsQuietAsync(CancellationToken cancellationToken) {
    var store = _store();
    var id = Guid.NewGuid();
    await store.UpsertAsync(id, _model("newer"), new PerspectiveScope(), false, _metadata(10), cancellationToken);
    var version = (await store.ReadForApplyAsync(id, cancellationToken)).Version;

    // Same version, older commit sequence: the row has already moved past this event.
    await store.UpsertAsync(id, _model("older"), new PerspectiveScope(), false, _metadata(2), version, cancellationToken);

    await Assert.That(await _nameAsync(id, cancellationToken)).IsEqualTo("newer")
      .Because("an event the row has already passed is skipped, not applied backwards");
  }

  /// <summary>An unchecked write still overwrites, which is what every existing caller relies on.</summary>
  [Test]
  [Timeout(120000)]
  public async Task Upsert_Unchecked_StillOverwritesAsync(CancellationToken cancellationToken) {
    var store = _store();
    var id = Guid.NewGuid();
    await store.UpsertAsync(id, _model("first"), new PerspectiveScope(), false, _metadata(1), cancellationToken);
    await store.UpsertAsync(id, _model("second"), new PerspectiveScope(), false, _metadata(2), cancellationToken);

    await Assert.That(await _nameAsync(id, cancellationToken)).IsEqualTo("second")
      .Because("a caller that passes no version is asking for the write this store always did");
  }

  /// <summary>A checked write that also sets physical columns is guarded like any other.</summary>
  /// <remarks>
  /// The physical-fields path builds its own statement, with the promoted columns in the same write, so
  /// it can carry the guard or drop it independently of the plain path. Dropped, a perspective with a
  /// promoted column is exactly the one that silently went back to overwriting concurrent writes.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task UpsertWithPhysicalFields_OnTheCurrentVersion_LandsAsync(CancellationToken cancellationToken) {
    var store = _store();
    var id = Guid.NewGuid();
    await store.UpsertWithPhysicalFieldsAsync(
      id, _model("first"), _physical("ops"), new PerspectiveScope(), false, _metadata(1), cancellationToken);

    var read = await store.ReadForApplyAsync(id, cancellationToken);
    await store.UpsertWithPhysicalFieldsAsync(
      id, _model("second"), _physical("support"), new PerspectiveScope(), false, _metadata(2),
      read.Version, cancellationToken);

    await Assert.That(await _nameAsync(id, cancellationToken)).IsEqualTo("second")
      .Because("nothing moved between the read and the write, so the write is the one that lands");
    await Assert.That(await _assignedToAsync(id, cancellationToken)).IsEqualTo("support")
      .Because("the promoted column is written in the same statement, so it lands with the model");
  }

  /// <summary>The same write on a version something else moved past is refused, columns and all.</summary>
  [Test]
  [Timeout(120000)]
  public async Task UpsertWithPhysicalFields_OnAStaleVersion_IsRefused_AndWritesNothingAsync(
      CancellationToken cancellationToken) {
    var store = _store();
    var id = Guid.NewGuid();
    await store.UpsertWithPhysicalFieldsAsync(
      id, _model("first"), _physical("ops"), new PerspectiveScope(), false, _metadata(1), cancellationToken);
    var stale = (await store.ReadForApplyAsync(id, cancellationToken)).Version;

    // Somebody else writes: xmin moves, and the version above is now history.
    await store.UpsertWithPhysicalFieldsAsync(
      id, _model("collective"), _physical("collective"), new PerspectiveScope(), false, _metadata(2),
      cancellationToken);

    await Assert.That(async () => await store.UpsertWithPhysicalFieldsAsync(
        id, _model("stale"), _physical("stale"), new PerspectiveScope(), false, _metadata(3),
        stale, cancellationToken))
      .Throws<PerspectiveRowConflictException>()
      .Because("the apply computed its model from a row that has since moved, so the write must not land");
    await Assert.That(await _assignedToAsync(id, cancellationToken)).IsEqualTo("collective")
      .Because("a refused write writes nothing -- the promoted column is not half-applied either");
  }

}
