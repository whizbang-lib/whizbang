// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2122, owner decision 4: the rollback itself. The elected migrator runs the reverse scripts the newer version stored
/// in the ledger, newest first, under the schema lock, in one transaction, saying how far along it is; each change it
/// reverses leaves the ledger, so the older version's own pass then migrates as usual. It re-reads the ledger under the
/// lock, so a rollback another instance finished is not run twice, and one the ledger no longer allows is refused.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaRollback.cs</code-under-test>
/// <docs>operations/startup/rolling-upgrades#rolling-back</docs>
[Category("Integration")]
[Category("Shard5")]
public class SchemaRollbackTests : EFCoreTestBase {
  private const int TIMEOUT_SECONDS = 30;

  private NpgsqlConnection _open() => new(ConnectionString);

  private async Task _execAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var connection = _open();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var (name, value) in args) {
      command.Parameters.AddWithValue(name, value);
    }
    await command.ExecuteNonQueryAsync(ct);
  }

  private async Task<T> _scalarAsync<T>(string sql, CancellationToken ct) {
    await using var connection = _open();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(sql, connection);
    return (T)(await command.ExecuteScalarAsync(ct))!;
  }

  private Task<string> _libraryAsync(CancellationToken ct) => _scalarAsync<string>("""
    SELECT v.library_version FROM wh_schema_migrations m JOIN wh_schema_versions v ON v.id = m.applied_version_id
    WHERE m.owner = 'whizbang' ORDER BY m.file_name DESC LIMIT 1
    """, ct);

  private Task<int> _versionAsync(string library, string application, CancellationToken ct) => _scalarAsync<int>($"""
    INSERT INTO wh_schema_versions (library_version, application_version) VALUES ('{library}', '{application}')
    ON CONFLICT (library_version, application_version) DO UPDATE SET notes = wh_schema_versions.notes
    RETURNING id
    """, ct);

  private Task _recordAsync(string name, string owner, int versionId, string reversal, string? down, CancellationToken ct) =>
    _execAsync("""
      INSERT INTO wh_schema_migrations (file_name, content_hash, version_id, status, owner, reversal, down_sql)
      VALUES (@name, 'h', @v, 1, @owner, @reversal, @down)
      """, ct, ("name", name), ("v", versionId), ("owner", owner), ("reversal", reversal), ("down", (object?)down ?? DBNull.Value));

  private SchemaRollbackRequest _request(string library, string application, bool allowLossy = false) =>
    new(_open, "public", SchemaInitializationLockKey.Compute("public"), new VersionSighting(library, application), allowLossy, TIMEOUT_SECONDS);

  [Test]
  [Timeout(60000)]
  public async Task EveryChangeSince_IsReversedNewestFirst_InOneTransaction_AndLeavesTheLedgerAsync(CancellationToken ct) {
    var library = await _libraryAsync(ct);
    var second = await _versionAsync(library, "2.0.0", ct);
    var third = await _versionAsync(library, "3.0.0", ct);
    await _execAsync("CREATE TABLE rb_log (seq serial, what text); CREATE TABLE rb_two (id int); CREATE TABLE rb_three (id int)", ct);
    await _recordAsync("wh_per_rb_two", "perspective", second, "reversible", "INSERT INTO rb_log (what) VALUES ('two'); DROP TABLE rb_two;", ct);
    await _recordAsync("wh_per_rb_three", "perspective", third, "reversible", "INSERT INTO rb_log (what) VALUES ('three'); DROP TABLE rb_three;", ct);
    await _recordAsync("association:rb", "association", third, "reversible", "", ct);
    var logger = new ListLogger();

    var reversed = await SchemaRollback.ApplyAsync(_request(library, "1.9.0"), new FakeTimeProvider(), logger, ct);

    await Assert.That(reversed).IsEqualTo(3);
    await Assert.That(await _scalarAsync<string>("SELECT string_agg(what, ',' ORDER BY seq) FROM rb_log", ct)).IsEqualTo("three,two")
      .Because("the newest version's changes are reversed first");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname IN ('rb_two', 'rb_three')", ct)).IsEqualTo(0L);
    await Assert.That(await _scalarAsync<long>(
      "SELECT count(*) FROM wh_schema_migrations WHERE file_name IN ('wh_per_rb_two', 'wh_per_rb_three', 'association:rb')", ct))
      .IsEqualTo(0L).Because("a reversed change is no longer applied; the older version's pass migrates from there");
    await Assert.That(logger.Messages).Contains(m => m.Contains("Rolling back schema public") && m.Contains("3 change(s)"));
    await Assert.That(logger.Messages).Contains(m => m.Contains("wh_per_rb_three") && m.Contains("(1/3)"));
    await Assert.That(logger.Messages).Contains(m => m.Contains("Rolled back schema public"));
  }

  [Test]
  [Timeout(60000)]
  public async Task AReverseScriptThatFails_RollsBackTheWholeRollback_NamingItAsync(CancellationToken ct) {
    var library = await _libraryAsync(ct);
    var newer = await _versionAsync(library, "2.0.0", ct);
    await _execAsync("CREATE TABLE rb_kept (id int)", ct);
    await _recordAsync("wh_per_rb_kept", "perspective", newer, "reversible", "DROP TABLE rb_kept;", ct);
    await _recordAsync("wh_per_rb_broken", "perspective", newer, "reversible", "DROP TABLE rb_does_not_exist;", ct);

    var failure = await Assert.That(async () => await SchemaRollback.ApplyAsync(_request(library, "1.9.0"), new FakeTimeProvider(), null, ct))
      .Throws<InvalidOperationException>();

    await Assert.That(failure!.Message).Contains("wh_per_rb_broken");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname = 'rb_kept'", ct)).IsEqualTo(1L)
      .Because("one transaction: a rollback that cannot finish changes nothing");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_schema_migrations WHERE file_name LIKE 'wh_per_rb_%'", ct))
      .IsEqualTo(2L);
  }

  [Test]
  [Timeout(60000)]
  public async Task NothingAppliedSince_ReversesNothingAsync(CancellationToken ct) {
    var library = await _libraryAsync(ct);

    await Assert.That(await SchemaRollback.ApplyAsync(_request(library, "unknown"), new FakeTimeProvider(), null, ct)).IsEqualTo(0)
      .Because("another instance finished the rollback while this one waited for the lock");
  }

  [Test]
  [Timeout(60000)]
  public async Task AnIrreversibleChangeUnderTheLock_IsRefused_NamingItAndTheVersionsAsync(CancellationToken ct) {
    var library = await _libraryAsync(ct);
    var newer = await _versionAsync(library, "2.0.0", ct);
    await _recordAsync("wh_per_rb_changed", "perspective", newer, "irreversible", null, ct);

    var failure = await Assert.That(async () => await SchemaRollback.ApplyAsync(_request(library, "1.9.0"), new FakeTimeProvider(), null, ct))
      .Throws<InvalidOperationException>();

    await Assert.That(failure!.Message).Contains($"wh_per_rb_changed (applied by library {library}, application 2.0.0)");
    await Assert.That(failure.Message).Contains("cannot be reversed");
  }

  [Test]
  [Timeout(60000)]
  public async Task ALossyChange_IsReversedOnlyWhenAllowedAsync(CancellationToken ct) {
    var library = await _libraryAsync(ct);
    var newer = await _versionAsync(library, "2.0.0", ct);
    await _execAsync("CREATE TABLE rb_lossy (id int)", ct);
    await _recordAsync("wh_per_rb_lossy", "perspective", newer, "lossy", "DROP TABLE rb_lossy;", ct);

    await Assert.That(async () => await SchemaRollback.ApplyAsync(_request(library, "1.9.0"), new FakeTimeProvider(), null, ct))
      .Throws<InvalidOperationException>().WithMessageContaining(UpgradeOrder.ALLOW_LOSSY_ROLLBACK_KEY);
    await Assert.That(await SchemaRollback.ApplyAsync(_request(library, "1.9.0", allowLossy: true), new FakeTimeProvider(), null, ct))
      .IsEqualTo(1);
  }

  [Test]
  [Timeout(60000)]
  public async Task ANewerVersionOnlineUnderTheLock_IsRefusedAsync(CancellationToken ct) {
    var library = await _libraryAsync(ct);
    var newer = await _versionAsync(library, "2.0.0", ct);
    await _recordAsync("wh_per_rb_online", "perspective", newer, "reversible", "SELECT 1;", ct);
    var peer = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    await _execAsync("""
      SELECT record_heartbeat(@id, 'svc', 'host', 1, '{}'::jsonb, 'Running', @library, 90, 'pooled', '2.0.0')
      """, ct, ("id", peer), ("library", library));

    await Assert.That(async () => await SchemaRollback.ApplyAsync(_request(library, "1.9.0"), new FakeTimeProvider(), null, ct))
      .Throws<InvalidOperationException>().WithMessageContaining("is online with application version 2.0.0");
  }

  [Test]
  public async Task ARequestWithoutWhatItNeeds_IsRefusedAsync() {
    await Assert.That(async () => await SchemaRollback.ApplyAsync(null!, new FakeTimeProvider(), null, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await SchemaRollback.ApplyAsync(_request("1.0.0", "1.0.0"), null!, null, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  private sealed class ListLogger : ILogger {
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages {
      get {
        lock (_messages) {
          return [.. _messages];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_messages) {
        _messages.Add(formatter(state, exception));
      }
    }
  }
}
