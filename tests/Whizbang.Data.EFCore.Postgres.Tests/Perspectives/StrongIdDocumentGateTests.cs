// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// #2518: a strongly-typed id in a perspective document is written as its UUID string, and the stored-form rewrite runs,
/// only once the whole fleet can take it. Each instance's heartbeat says it reads the UUID string (stored form level 2;
/// a release before says 1 or nothing) and, once it does, that it writes it. The schema's gate
/// (<c>wh_strong_id_document_gate</c>) latches once every live instance reads it, which switches this process's writes;
/// the rewrite waits until every live instance also writes it. A version comparison decides nothing: two prereleases
/// of one release differ only in what they announce.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/StrongIdDocumentGate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/562_StrongIdsStoredAsUuidStrings.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StrongIdDocumentGateTests : EFCoreTestBase {
  private const string READS = """{"stored_form_level":2}""";
  private const string WRITES = """{"stored_form_level":2,"strong_id_document_form":"uuid-string"}""";
  private const string EARLIER = """{"stored_form_level":1}""";
  private static readonly DateTime _written = new(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);

  [Test]
  public async Task AFreshService_StaysClosed_UntilAnInstanceThatReadsTheUuidStringIsLiveAsync() {
    await using var conn = await _openAsync();

    var empty = await StrongIdDocumentGate.CheckAsync(conn, "public");
    await _registerThisBuildAsync();
    var registered = await StrongIdDocumentGate.CheckAsync(conn, "public");

    await Assert.That(empty.State).IsEqualTo(StrongIdDocumentGateState.Closed);
    await Assert.That(empty.Status).IsEqualTo("closed (0 of 0 live instances read strong ids as UUID strings)");
    await Assert.That(registered.Status).IsEqualTo("opened").Because("the one live instance is this build, whose heartbeat says level 2");
    await Assert.That(await _scalarAsync(conn, "SELECT metadata ->> 'stored_form_level' FROM wh_service_instances LIMIT 1"))
      .IsEqualTo("2").Because("the EF Core driver's heartbeat wrote this build's level");
    await Assert.That(await _flagAsync(conn)).IsEqualTo("true");
    await Assert.That(WhizbangIdDocumentForm.IsOpen(conn.Database, "public")).IsTrue()
      .Because("this process's writes switch with the gate");
  }

  /// <summary>
  /// An instance of an earlier release holds both the write form and the rewrite; so does one of an earlier prerelease of
  /// the same release, whose numeric version is the same: what it announces decides, not its version (#2518).
  /// </summary>
  [Test]
  [Arguments("0.4522.0", "0.4523.0")]
  [Arguments("0.4522.0-alpha.356", "0.4522.0-alpha.357")]
  public async Task AnInstanceThatDoesNotReadTheUuidString_HoldsTheWriteFormAndTheRewrite_WhateverItsVersionAsync(
      string earlier, string current) {
    await using var conn = await _openAsync();
    var older = await _registerAsync(conn, EARLIER, earlier);
    await _registerAsync(conn, WRITES, current);

    var gate = await StrongIdDocumentGate.CheckAsync(conn, "public");
    var hold = await StrongIdDocumentGate.RewriteHoldAsync(conn, "public");
    var run = await _runRewriteAsync();

    await Assert.That(gate.State).IsEqualTo(StrongIdDocumentGateState.Closed);
    await Assert.That(gate.Status).IsEqualTo("closed (1 of 2 live instances read strong ids as UUID strings)");
    await Assert.That(WhizbangIdDocumentForm.IsOpen(conn.Database, "public")).IsFalse()
      .Because($"an instance of {earlier} is live, whose Entity Framework reader cannot read the UUID string");
    await Assert.That(WhizbangIdDocumentForm.ProcessWritesUuidString).IsFalse()
      .Because("a schema this process writes to is held, so the process writes the object form");
    await Assert.That(hold).IsNotNull();
    await Assert.That(run.Outcome).IsEqualTo(BatchedRewriteOutcome.HeldForFleet);
    await Assert.That(await _rewriteRanAsync(conn)).IsFalse();
    await Assert.That(await _flagAsync(conn)).IsNull().Because("while closed, nothing is latched");

    await _executeAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @i", ("i", older));
    var after = await StrongIdDocumentGate.CheckAsync(conn, "public");

    await Assert.That(after.Status).IsEqualTo("opened").Because("the last instance that could not read it has left");
    await Assert.That(WhizbangIdDocumentForm.IsOpen(conn.Database, "public")).IsTrue();
  }

  /// <summary>
  /// While an instance that cannot read the UUID string is live, neither writer of a document writes it: the atomic upsert
  /// and Entity Framework both store the id in the object form every release reads (#2518).
  /// </summary>
  [Test]
  public async Task WhileAnEarlierInstanceIsLive_NeitherWriterWritesTheUuidStringAsync() {
    await using var conn = await _openAsync();
    await _registerAsync(conn, EARLIER, "0.4522.0-alpha.356");
    await _registerAsync(conn, WRITES, "0.4522.0-alpha.357");
    await Assert.That((await StrongIdDocumentGate.CheckAsync(conn, "public")).State).IsEqualTo(StrongIdDocumentGateState.Closed);
    var upserted = (Guid)TrackedGuid.New();
    var added = (Guid)TrackedGuid.New();

    await using (var context = CreateDbContext()) {
      await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(context, "wh_per_order", upserted,
        new Order { OrderId = new TestOrderId(upserted), Amount = 1m, Status = "atomic" },
        new PerspectiveMetadata { EventType = "OrderCreated", EventId = TrackedGuid.New().ToString(), Timestamp = _written },
        new PerspectiveScope());
    }
    await using (var context = CreateDbContext()) {
      context.Set<PerspectiveRow<Order>>().Add(new PerspectiveRow<Order> {
        Id = added,
        Data = new Order { OrderId = new TestOrderId(added), Amount = 2m, Status = "ef" },
        Metadata = new PerspectiveMetadata { EventType = "OrderCreated", EventId = TrackedGuid.New().ToString(), Timestamp = _written },
        Scope = new PerspectiveScope(),
        CreatedAt = _written,
        UpdatedAt = _written,
        Version = 1,
      });
      await context.SaveChangesAsync();
    }

    await Assert.That(await _scalarAsync(conn, $"SELECT jsonb_typeof(data -> 'OrderId') FROM wh_per_order WHERE id = '{upserted}'"))
      .IsEqualTo("object").Because("the atomic upsert writes the form the earlier release reads");
    await Assert.That(await _scalarAsync(conn, $"SELECT jsonb_typeof(data -> 'OrderId') FROM wh_per_order WHERE id = '{added}'"))
      .IsEqualTo("object").Because("so does Entity Framework");
    await using var reader = CreateDbContext();
    await Assert.That(await reader.Set<PerspectiveRow<Order>>().AsNoTracking()
      .Where(r => r.Data.OrderId == new TestOrderId(added)).Select(r => r.Id).ToListAsync()).IsEquivalentTo([added]);
  }

  [Test]
  public async Task AStaleEarlierInstance_IsNotLive_AndHoldsNothingAsync() {
    await using var conn = await _openAsync();
    var older = await _registerAsync(conn, EARLIER, "0.4522.0-alpha.356");
    await _registerAsync(conn, READS, "0.4522.0-alpha.357");
    await _executeAsync(conn, "UPDATE wh_service_instances SET last_heartbeat_at = now() - interval '1 hour' WHERE instance_id = @i",
      ("i", older));

    await Assert.That((await StrongIdDocumentGate.CheckAsync(conn, "public")).Status).IsEqualTo("opened");
  }

  /// <summary>
  /// Once open, the rewrite still waits for every live instance to write the UUID string, so none writes the object form
  /// behind it; it runs once they all do (#2518).
  /// </summary>
  [Test]
  public async Task TheRewrite_WaitsUntilEveryLiveInstanceWritesTheUuidString_ThenRunsAsync() {
    await using var conn = await _openAsync();
    var reader = await _registerAsync(conn, READS, "0.4522.0");
    await _registerAsync(conn, WRITES, "0.4522.0");
    await Assert.That((await StrongIdDocumentGate.CheckAsync(conn, "public")).Status).IsEqualTo("opened");

    var open = await StrongIdDocumentGate.CheckAsync(conn, "public");
    var held = await _runRewriteAsync();

    await Assert.That(open.State).IsEqualTo(StrongIdDocumentGateState.Open);
    await Assert.That(open.Status).IsEqualTo("open (1 of 2 live instances write strong ids as UUID strings)");
    await Assert.That(held.Outcome).IsEqualTo(BatchedRewriteOutcome.HeldForFleet);
    await Assert.That(await _rewriteRanAsync(conn)).IsFalse();

    await _registerAsync(conn, WRITES, "0.4522.0", reader);
    var ready = await StrongIdDocumentGate.CheckAsync(conn, "public");
    await _runRewriteAsync();

    await Assert.That(ready.State).IsEqualTo(StrongIdDocumentGateState.Ready);
    await Assert.That(await _rewriteRanAsync(conn)).IsTrue();
  }

  /// <summary>The latch is one-way: an instance that cannot read the UUID string starting later does not close it.</summary>
  [Test]
  public async Task OnceOpen_TheGateStaysOpen_WhenAnEarlierInstanceAppearsAsync() {
    await using var conn = await _openAsync();
    await _registerAsync(conn, WRITES, "0.4522.0");
    await Assert.That((await StrongIdDocumentGate.CheckAsync(conn, "public")).Status).IsEqualTo("opened");

    await _registerAsync(conn, EARLIER, "0.4521.0");
    var after = await StrongIdDocumentGate.CheckAsync(conn, "public");

    await Assert.That(after.State).IsEqualTo(StrongIdDocumentGateState.Open);
    await Assert.That(WhizbangIdDocumentForm.IsOpen(conn.Database, "public")).IsTrue();
  }

  [Test]
  public async Task AFlagWrittenByHandOtherThanTrue_IsNotOpenAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn,
      "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('strong_id_uuid_string_documents', 'false', 'boolean')");
    await _registerAsync(conn, EARLIER, "0.4521.0");

    await Assert.That((await StrongIdDocumentGate.CheckAsync(conn, "public")).State).IsEqualTo(StrongIdDocumentGateState.Closed);
  }

  [Test]
  public async Task ASchemaWithoutTheGate_IsUnavailable_AndHeldAsync() {
    await using var conn = await _openAsync();

    var answer = await StrongIdDocumentGate.CheckAsync(conn, "no_such_schema");

    await Assert.That(answer.State).IsEqualTo(StrongIdDocumentGateState.Unavailable);
    await Assert.That(WhizbangIdDocumentForm.IsOpen(conn.Database, "no_such_schema")).IsFalse();
  }

  [Test]
  [Arguments("ready", StrongIdDocumentGateState.Ready)]
  [Arguments("opened", StrongIdDocumentGateState.Open)]
  [Arguments("open (1 of 2 live instances write strong ids as UUID strings)", StrongIdDocumentGateState.Open)]
  [Arguments("closed (0 of 0 live instances read strong ids as UUID strings)", StrongIdDocumentGateState.Closed)]
  public async Task StateOf_ReadsTheGatesWordsAsync(string status, StrongIdDocumentGateState expected) =>
    await Assert.That(StrongIdDocumentGate.StateOf(status)).IsEqualTo(expected);

  // ===== Helpers =====

  /// <summary>Registers an instance of this build through the EF Core driver's heartbeat.</summary>
  private async Task _registerThisBuildAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var accepted = await coordinator.RecordHeartbeatAsync(new HeartbeatRequest((Guid)TrackedGuid.New(), "svc", "this-build", 1));
    await Assert.That(accepted).IsTrue();
  }

  /// <summary>Registers, or beats again for, a live instance whose heartbeat says <paramref name="metadata"/>.</summary>
  private static async Task<Guid> _registerAsync(NpgsqlConnection conn, string metadata, string libraryVersion, Guid? instance = null) {
    var id = instance ?? (Guid)TrackedGuid.New();
    await _executeAsync(conn, "SELECT record_heartbeat(@i, 'svc', 'host', 2, @m::jsonb, NULL, @v)",
      ("i", id), ("m", metadata), ("v", libraryVersion));
    return id;
  }

  /// <summary>
  /// One batch of a stand-in rewrite under the strong-id gate: its statement marks that it ran, so the test reads whether
  /// the gate let it.
  /// </summary>
  private async Task<BatchedRewriteRun> _runRewriteAsync() =>
    await BatchedRewriteRunner.RunAsync(
      new BatchedRewriteRequest(
        () => new NpgsqlConnection(ConnectionString), "public", BatchedRewriteLockKey.Compute("public"),
        [("strong-id-scalar:gate_probe:0", "CREATE TABLE IF NOT EXISTS strong_id_gate_probe_ran (ran boolean)")], 60),
      new BatchedRewriteBudget(1, TimeSpan.FromDays(1)),
      (connection, ct) => StrongIdDocumentGate.RewriteHoldAsync(connection, "public", ct),
      applier: null,
      new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

  private static async Task<bool> _rewriteRanAsync(NpgsqlConnection conn) =>
    await _scalarAsync(conn, "SELECT to_regclass('strong_id_gate_probe_ran') IS NOT NULL") == "True";

  private static Task<string?> _flagAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT setting_value FROM wh_settings WHERE setting_key = 'strong_id_uuid_string_documents'");

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<string?> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    return await cmd.ExecuteScalarAsync() is { } value and not DBNull ? value.ToString() : null;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
