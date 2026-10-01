using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 173: the role assignment. The advisory lock decides only the vote; the row is the
/// role from then on, carrying an epoch that exclusive work presents as a fencing token. Every
/// bound is computed from the database's <c>now()</c>, so these tests move time by shifting the
/// stored timestamps back, which is exactly what the database clock moving forward looks like to
/// every comparison the functions make. Nothing here sleeps.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/173_RoleAssignments.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RoleAssignmentSqlTests : EFCoreTestBase {
  private const string ROLE = "maintainer";
  private static readonly TimeSpan _lease = TimeSpan.FromSeconds(15);
  private static readonly TimeSpan _cooldown = TimeSpan.FromSeconds(15);

  private sealed record Vote(
    string Outcome, Guid? Holder, long Epoch, TimeSpan? LeaseRemaining, Guid? PreviousHolder, string? VoidReason);

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task<Guid> _joinAsync(CancellationToken ct) {
    var instanceId = (Guid)TrackedGuid.New();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "role-svc", "role-host", 1), ct);
    return instanceId;
  }

  private async Task<Vote> _electAsync(Guid instanceId, CancellationToken ct, long? legacyKey = null) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT outcome, holder_instance_id, epoch, lease_remaining, previous_holder_instance_id, void_reason "
      + "FROM wh_elect_role(@role, @id, @lease, @cooldown, @legacy)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("lease", _lease);
    cmd.Parameters.AddWithValue("cooldown", _cooldown);
    cmd.Parameters.Add(new NpgsqlParameter("legacy", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)legacyKey ?? DBNull.Value });
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    await Assert.That(await reader.ReadAsync(ct)).IsTrue();
    return new Vote(
      reader.GetString(0),
      await reader.IsDBNullAsync(1, ct) ? null : reader.GetGuid(1),
      reader.GetInt64(2),
      await reader.IsDBNullAsync(3, ct) ? null : reader.GetTimeSpan(3),
      await reader.IsDBNullAsync(4, ct) ? null : reader.GetGuid(4),
      await reader.IsDBNullAsync(5, ct) ? null : reader.GetString(5));
  }

  private async Task<bool> _scalarBoolAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private Task<bool> _renewAsync(Guid instanceId, long epoch, CancellationToken ct) =>
    _scalarBoolAsync("SELECT wh_renew_role(@role, @id, @epoch)", ct, ("role", ROLE), ("id", instanceId), ("epoch", epoch));

  private Task<bool> _releaseAsync(Guid instanceId, long epoch, CancellationToken ct) =>
    _scalarBoolAsync("SELECT wh_release_role(@role, @id, @epoch)", ct, ("role", ROLE), ("id", instanceId), ("epoch", epoch));

  /// <summary>Moves the database clock forward for this role by moving its stored instants back.</summary>
  private async Task _ageAsync(TimeSpan by, CancellationToken ct) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_role_assignments
         SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
             lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
       WHERE role = @role
      """;
    cmd.Parameters.AddWithValue(nameof(by), by);
    cmd.Parameters.AddWithValue("role", ROLE);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private async Task<long> _electionCountAsync(CancellationToken ct) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COALESCE((SELECT election_count FROM wh_role_assignments WHERE role = @role), 0)";
    cmd.Parameters.AddWithValue("role", ROLE);
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private async Task _executeAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync(ct);
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_VacantRole_GrantsEpochOne_WithALeaseInDatabaseTime_AndRecordsTheCapabilityAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);

    var vote = await _electAsync(a, cancellationToken);

    await Assert.That(vote.Outcome).IsEqualTo("granted");
    await Assert.That(vote.Holder).IsEqualTo(a);
    await Assert.That(vote.Epoch).IsEqualTo(1L);
    await Assert.That(vote.LeaseRemaining).IsNotNull();
    await Assert.That(vote.LeaseRemaining!.Value).IsLessThanOrEqualTo(_lease)
      .Because("the remaining lease is a duration computed by the database, never an instant for the caller's clock");
    await Assert.That(vote.LeaseRemaining.Value).IsGreaterThan(TimeSpan.Zero);
    await Assert.That(vote.PreviousHolder).IsNull();
    await Assert.That(await _scalarBoolAsync(
        "SELECT EXISTS(SELECT 1 FROM wh_instance_capabilities WHERE instance_id = @id AND capability = @role)",
        cancellationToken, ("id", a), ("role", ROLE))).IsTrue()
      .Because("the capability row still reports the holding, so every surface reading holdings keeps working");
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_ByTheCurrentHolder_AnswersHeld_WithTheSameEpochAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var first = await _electAsync(a, cancellationToken);
    await _ageAsync(TimeSpan.FromSeconds(10), cancellationToken);

    var again = await _electAsync(a, cancellationToken);

    await Assert.That(again.Outcome).IsEqualTo("held");
    await Assert.That(again.Epoch).IsEqualTo(first.Epoch)
      .Because("asking again is not a new assignment: the epoch only moves when the holder changes");
    await Assert.That(again.LeaseRemaining!.Value).IsGreaterThan(TimeSpan.FromSeconds(10))
      .Because("the holder asking again renews its lease from now()");
    await Assert.That(await _electionCountAsync(cancellationToken)).IsEqualTo(1L);
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_WhileAnotherHoldsALiveLease_IsContended_AndWritesNothingAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var granted = await _electAsync(a, cancellationToken);

    var vote = await _electAsync(b, cancellationToken);

    await Assert.That(vote.Outcome).IsEqualTo("contended");
    await Assert.That(vote.Holder).IsEqualTo(a);
    await Assert.That(vote.Epoch).IsEqualTo(granted.Epoch);
    await Assert.That(await _electionCountAsync(cancellationToken)).IsEqualTo(1L);
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_AfterTheHoldersLeaseLapses_VoidsIt_AndGrantsTheNextEpochAsync(CancellationToken cancellationToken) {
    // Requirement 2: a crashed holder simply stops renewing. No session, no TCP timeout: the
    // lease lapses in database time and the next vote voids it.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var first = await _electAsync(a, cancellationToken);

    await _ageAsync(_lease - TimeSpan.FromSeconds(1), cancellationToken);
    var beforeTheBound = await _electAsync(b, cancellationToken);
    await Assert.That(beforeTheBound.Outcome).IsEqualTo("contended")
      .Because("inside the lease the holder is presumed alive; the bound is the lease, not a guess");

    await _ageAsync(TimeSpan.FromSeconds(2), cancellationToken);
    var takeover = await _electAsync(b, cancellationToken);

    await Assert.That(takeover.Outcome).IsEqualTo("granted");
    await Assert.That(takeover.Holder).IsEqualTo(b);
    await Assert.That(takeover.Epoch).IsEqualTo(first.Epoch + 1);
    await Assert.That(takeover.PreviousHolder).IsEqualTo(a);
    await Assert.That(takeover.VoidReason).IsEqualTo("lapsed");
    await Assert.That(await _scalarBoolAsync(
        "SELECT EXISTS(SELECT 1 FROM wh_instance_capabilities WHERE instance_id = @id AND capability = @role)",
        cancellationToken, ("id", a), ("role", ROLE))).IsFalse()
      .Because("the voided holder's capability row stops reporting a holding it no longer has");
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_ByALapsedHolder_WithinTheCooldown_IsRefused_ButAnotherInstanceIsGrantedAsync(CancellationToken cancellationToken) {
    // Requirement 5: a slow instance that lapsed must not win the role back the moment it recovers.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    _ = await _electAsync(a, cancellationToken);
    await _ageAsync(_lease + TimeSpan.FromSeconds(1), cancellationToken);

    var comeback = await _electAsync(a, cancellationToken);
    await Assert.That(comeback.Outcome).IsEqualTo("cooling_down");
    await Assert.That(comeback.Holder).IsNull()
      .Because("the lapse was recorded even though the caller was refused: the role is vacant");

    var other = await _electAsync(b, cancellationToken);
    await Assert.That(other.Outcome).IsEqualTo("granted")
      .Because("the cool-down applies to the demoted instance only; everyone else may take the role");
    await Assert.That(other.PreviousHolder).IsEqualTo(a);
    await Assert.That(other.VoidReason).IsEqualTo("lapsed")
      .Because("the grant reports the vacancy it filled, even one an earlier refused vote recorded");
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_ByALapsedHolder_AfterTheCooldown_IsGrantedAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var first = await _electAsync(a, cancellationToken);
    await _ageAsync(_lease + TimeSpan.FromSeconds(1), cancellationToken);
    await Assert.That((await _electAsync(a, cancellationToken)).Outcome).IsEqualTo("cooling_down");

    await _ageAsync(_cooldown + TimeSpan.FromSeconds(1), cancellationToken);
    var afterCooldown = await _electAsync(a, cancellationToken);

    await Assert.That(afterCooldown.Outcome).IsEqualTo("granted");
    await Assert.That(afterCooldown.Epoch).IsEqualTo(first.Epoch + 1)
      .Because("winning back a role is a new assignment, so the old epoch stays fenced");
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_WhenTheHolderIsTombstoned_VoidsItAsEvictedAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    _ = await _electAsync(a, cancellationToken);
    await _executeAsync("INSERT INTO wh_instance_evictions (instance_id, reason) VALUES (@id, 'test')", cancellationToken, ("id", a));

    var vote = await _electAsync(b, cancellationToken);

    await Assert.That(vote.Outcome).IsEqualTo("granted");
    await Assert.That(vote.VoidReason).IsEqualTo("evicted")
      .Because("eviction voids the assignment even inside its lease");
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_WhenTheHolderIsNoLongerRegistered_VoidsItAsUnregisteredAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    _ = await _electAsync(a, cancellationToken);
    await _executeAsync("DELETE FROM wh_service_instances WHERE instance_id = @id", cancellationToken, ("id", a));

    var vote = await _electAsync(b, cancellationToken);

    await Assert.That(vote.Outcome).IsEqualTo("granted");
    await Assert.That(vote.VoidReason).IsEqualTo("unregistered");
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_ByAnEvictedOrUnregisteredCaller_IsRefusedAsync(CancellationToken cancellationToken) {
    var evicted = await _joinAsync(cancellationToken);
    await _executeAsync("INSERT INTO wh_instance_evictions (instance_id, reason) VALUES (@id, 'test')", cancellationToken, ("id", evicted));

    await Assert.That((await _electAsync(evicted, cancellationToken)).Outcome).IsEqualTo("refused");
    await Assert.That((await _electAsync((Guid)TrackedGuid.New(), cancellationToken)).Outcome).IsEqualTo("refused")
      .Because("an instance the registry does not know cannot hold a role");
    await Assert.That(await _electionCountAsync(cancellationToken)).IsEqualTo(0L);
  }

  [Test]
  [Timeout(60000)]
  public async Task Elect_WhileAnotherBackendHoldsTheLegacySessionLock_AnswersLegacyHolderAsync(CancellationToken cancellationToken) {
    // Mixed-version fleets: an instance still on the session-lock elector holds the duty's lock.
    var a = await _joinAsync(cancellationToken);
    const long legacyKey = 7_966_001L;
    await using var legacy = await _openAsync(cancellationToken);
    await using (var take = legacy.CreateCommand()) {
      take.CommandText = $"SELECT pg_advisory_lock({legacyKey})";
      await take.ExecuteNonQueryAsync(cancellationToken);
    }

    var vote = await _electAsync(a, cancellationToken, legacyKey);

    await Assert.That(vote.Outcome).IsEqualTo("legacy_holder");
    await Assert.That(await _electionCountAsync(cancellationToken)).IsEqualTo(0L);
  }

  [Test]
  [Timeout(60000)]
  public async Task Renew_ExtendsOnlyTheCurrentLiveAssignmentAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var vote = await _electAsync(a, cancellationToken);
    await _ageAsync(TimeSpan.FromSeconds(10), cancellationToken);

    await Assert.That(await _renewAsync(a, vote.Epoch, cancellationToken)).IsTrue();
    await Assert.That(await _renewAsync(a, vote.Epoch + 1, cancellationToken)).IsFalse()
      .Because("a renewal presents the epoch too; a different epoch is not this assignment");

    await _ageAsync(_lease + TimeSpan.FromSeconds(1), cancellationToken);
    await Assert.That(await _renewAsync(a, vote.Epoch, cancellationToken)).IsFalse()
      .Because("a lapsed lease is void; the holder must win a new vote, not revive the old one");
  }

  [Test]
  [Timeout(60000)]
  public async Task Renew_ByATombstonedHolder_IsRefusedAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var vote = await _electAsync(a, cancellationToken);
    await _executeAsync("INSERT INTO wh_instance_evictions (instance_id, reason) VALUES (@id, 'test')", cancellationToken, ("id", a));

    await Assert.That(await _renewAsync(a, vote.Epoch, cancellationToken)).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task Release_VacatesAtOnce_WithNoCooldownForTheReleaserAsync(CancellationToken cancellationToken) {
    // Requirement 6: a graceful release hands off at once rather than after the lease lapses.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var vote = await _electAsync(a, cancellationToken);

    await Assert.That(await _releaseAsync(a, vote.Epoch + 1, cancellationToken)).IsFalse()
      .Because("only the current epoch can release; a stale grant cannot vacate its successor");
    await Assert.That(await _releaseAsync(a, vote.Epoch, cancellationToken)).IsTrue();

    var takeover = await _electAsync(b, cancellationToken);
    await Assert.That(takeover.Outcome).IsEqualTo("granted");
    await Assert.That(takeover.Epoch).IsEqualTo(vote.Epoch + 1);
    await Assert.That(takeover.PreviousHolder).IsEqualTo(a);
    await Assert.That(takeover.VoidReason).IsEqualTo("released");
    await _releaseAsync(b, takeover.Epoch, cancellationToken);
    await Assert.That((await _electAsync(a, cancellationToken)).Outcome).IsEqualTo("granted")
      .Because("a release is not a health signal, so the releaser is not cooled down");
  }

  [Test]
  [Timeout(60000)]
  public async Task AssertEpoch_WithAStaleEpoch_RaisesInSql_AndTheWriteBesideItRollsBackAsync(CancellationToken cancellationToken) {
    // Requirement 1: a paused holder that wakes after losing the role presents a stale epoch, and
    // the database refuses it. Nothing client-side is trusted.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var stale = await _electAsync(a, cancellationToken);
    await _ageAsync(_lease + TimeSpan.FromSeconds(1), cancellationToken);
    var current = await _electAsync(b, cancellationToken);
    await _executeAsync("CREATE TABLE fenced_work (written_by UUID NOT NULL)", cancellationToken);

    await using var conn = await _openAsync(cancellationToken);
    PostgresException? refused = null;
    await using (var tx = await conn.BeginTransactionAsync(cancellationToken)) {
      await using var write = conn.CreateCommand();
      write.Transaction = tx;
      write.CommandText = "INSERT INTO fenced_work VALUES (@id); SELECT wh_assert_role_epoch(@role, @id, @epoch);";
      write.Parameters.AddWithValue("id", a);
      write.Parameters.AddWithValue("role", ROLE);
      write.Parameters.AddWithValue("epoch", stale.Epoch);
      try {
        await write.ExecuteNonQueryAsync(cancellationToken);
      } catch (PostgresException ex) {
        refused = ex;
      }
    }

    await Assert.That(refused).IsNotNull();
    await Assert.That(refused!.SqlState).IsEqualTo("WHF01");
    await using (var count = conn.CreateCommand()) {
      count.CommandText = "SELECT count(*) FROM fenced_work";
      await Assert.That((long)(await count.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(0L)
        .Because("the stale holder's write shared the fenced transaction, so it never committed");
    }

    await _executeAsync("INSERT INTO fenced_work VALUES (@id); SELECT wh_assert_role_epoch(@role, @id, @epoch);",
      cancellationToken, ("id", b), ("role", ROLE), ("epoch", current.Epoch));
    await using (var count = conn.CreateCommand()) {
      count.CommandText = "SELECT count(*) FROM fenced_work WHERE written_by = @b";
      count.Parameters.AddWithValue("b", b);
      await Assert.That((long)(await count.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(1L);
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task AssertEpoch_RefusesTheRightEpochFromTheWrongHolder_ALapsedLease_AndAnUnknownRoleAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var vote = await _electAsync(a, cancellationToken);

    async Task<string?> fenceAsync(string role, Guid id, long epoch) {
      try {
        await _executeAsync("SELECT wh_assert_role_epoch(@role, @id, @epoch)", cancellationToken,
          ("role", role), ("id", id), ("epoch", epoch));
        return null;
      } catch (PostgresException ex) {
        return ex.SqlState;
      }
    }

    await Assert.That(await fenceAsync(ROLE, a, vote.Epoch)).IsNull();
    await Assert.That(await fenceAsync(ROLE, b, vote.Epoch)).IsEqualTo("WHF01")
      .Because("an epoch reissued after a failover must still fail for the wrong holder");
    await Assert.That(await fenceAsync("no-such-role", a, vote.Epoch)).IsEqualTo("WHF01");
    await _ageAsync(_lease + TimeSpan.FromSeconds(1), cancellationToken);
    await Assert.That(await fenceAsync(ROLE, a, vote.Epoch)).IsEqualTo("WHF01")
      .Because("a lapsed lease fences its holder even before anyone re-votes");
  }

  [Test]
  [Timeout(60000)]
  public async Task Release_NotifiesWaiters_SoTheyReVoteAtOnceAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var vote = await _electAsync(a, cancellationToken);
    await using var listener = await _openAsync(cancellationToken);
    string? payload = null;
    listener.Notification += (_, e) => payload = e.Channel == "wh_role_released" ? e.Payload : payload;
    await using (var listen = listener.CreateCommand()) {
      listen.CommandText = "LISTEN wh_role_released";
      await listen.ExecuteNonQueryAsync(cancellationToken);
    }

    await _releaseAsync(a, vote.Epoch, cancellationToken);
    var delivered = await listener.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

    await Assert.That(delivered).IsTrue().Because("the release commits its NOTIFY with it");
    await Assert.That(payload).IsEqualTo(ROLE);
  }

  private async Task _oweAsync(string key, CancellationToken ct) =>
    await _executeAsync("SELECT wh_owe_role_work(@role, @key)", ct, ("role", ROLE), ("key", key));

  private sealed record Owed(string Key, DateTime FirstOwedAt, DateTime LastOwedAt, int Attempts, string? LastError, bool Due);

  private async Task<List<Owed>> _owedAsync(CancellationToken ct, TimeSpan? retryBase = null) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT work_key, first_owed_at, last_owed_at, attempts, last_error, due FROM wh_owed_role_work(@role, @base)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue("base", retryBase ?? TimeSpan.FromSeconds(30));
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    var rows = new List<Owed>();
    while (await reader.ReadAsync(ct)) {
      rows.Add(new Owed(reader.GetString(0), reader.GetDateTime(1), reader.GetDateTime(2), reader.GetInt32(3),
        await reader.IsDBNullAsync(4, ct) ? null : reader.GetString(4), reader.GetBoolean(5)));
    }
    return rows;
  }

  private async Task<bool> _completeAsync(Guid id, long epoch, string key, DateTime listedOwedAt, CancellationToken ct) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_complete_role_work(@role, @key, @id, @epoch, @listed)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue(nameof(key), key);
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.AddWithValue("listed", listedOwedAt);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  [Test]
  [Timeout(60000)]
  public async Task OweRoleWork_IsIdempotent_AndReOwingKeepsFirstOwedAtAsync(CancellationToken cancellationToken) {
    await _oweAsync("Rewrite", cancellationToken);
    var first = (await _owedAsync(cancellationToken)).Single();
    await _oweAsync("Rewrite", cancellationToken);
    await _oweAsync("Other", cancellationToken);

    var owed = await _owedAsync(cancellationToken);
    await Assert.That(owed.Select(o => o.Key)).IsEquivalentTo(["Rewrite", "Other"]);
    var again = owed.Single(o => o.Key == "Rewrite");
    await Assert.That(again.FirstOwedAt).IsEqualTo(first.FirstOwedAt)
      .Because("how long work has been owed is measured from the first time anyone owed it");
    await Assert.That(again.LastOwedAt).IsGreaterThan(first.LastOwedAt);
    await Assert.That(again.Due).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task CompleteRoleWork_IsFenced_AndKeepsWorkReOwedDuringTheRunAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var vote = await _electAsync(a, cancellationToken);
    await _oweAsync("Rewrite", cancellationToken);
    var listed = (await _owedAsync(cancellationToken)).Single();

    // Someone owes it again while the holder is running it: that newer need must survive.
    await _oweAsync("Rewrite", cancellationToken);
    await Assert.That(await _completeAsync(a, vote.Epoch, "Rewrite", listed.LastOwedAt, cancellationToken)).IsFalse();
    var relisted = (await _owedAsync(cancellationToken)).Single();
    await Assert.That(await _completeAsync(a, vote.Epoch, "Rewrite", relisted.LastOwedAt, cancellationToken)).IsTrue();
    await Assert.That(await _owedAsync(cancellationToken)).IsEmpty();

    await _oweAsync("Rewrite", cancellationToken);
    var owed = (await _owedAsync(cancellationToken)).Single();
    await Assert.That(async () => await _completeAsync(b, vote.Epoch, "Rewrite", owed.LastOwedAt, cancellationToken))
      .Throws<PostgresException>().Because("only the holder, at its epoch, may mark duty work done");
    await Assert.That(await _owedAsync(cancellationToken)).Count().IsEqualTo(1);
  }

  [Test]
  [Timeout(60000)]
  public async Task OwedRoleWork_BacksOffAFailedAttempt_InDatabaseTimeAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var vote = await _electAsync(a, cancellationToken);
    await _oweAsync("Rewrite", cancellationToken);

    await _executeAsync("SELECT wh_fail_role_work(@role, 'Rewrite', @id, @epoch, 'table locked')", cancellationToken,
      ("role", ROLE), ("id", a), ("epoch", vote.Epoch));
    var failed = (await _owedAsync(cancellationToken)).Single();
    await Assert.That(failed.Attempts).IsEqualTo(1);
    await Assert.That(failed.LastError).IsEqualTo("table locked");
    await Assert.That(failed.Due).IsFalse().Because("a failed attempt backs off before the holder tries again");

    await _executeAsync("UPDATE wh_role_pending_work SET last_attempt_at = last_attempt_at - interval '31 seconds'", cancellationToken);
    await Assert.That((await _owedAsync(cancellationToken)).Single().Due).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task FailRoleWork_IsFencedAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var vote = await _electAsync(a, cancellationToken);
    await _oweAsync("Rewrite", cancellationToken);

    await Assert.That(async () => await _executeAsync("SELECT wh_fail_role_work(@role, 'Rewrite', @id, @epoch, 'x')", cancellationToken,
      ("role", ROLE), ("id", a), ("epoch", vote.Epoch + 1))).Throws<PostgresException>();
    await Assert.That((await _owedAsync(cancellationToken)).Single().Attempts).IsEqualTo(0);
  }

  [Test]
  [Timeout(60000)]
  public async Task VoteLockKey_CarriesThisSchemasTableOid_SoItIsSchemaScopedByConstructionAsync(CancellationToken cancellationToken) {
    // Issue #962: a role's vote in one schema must never wait on the same role in another. The
    // key's high half is the oid of this schema's table, which no other schema's table shares.
    var sameAsTableOid = await _scalarBoolAsync("""
      SELECT ((_role_vote_lock_key(@role) >> 32) & 4294967295) = 'wh_role_assignments'::regclass::oid::bigint
         AND (_role_vote_lock_key(@role) & 4294967295) = (hashtext(@role)::bigint & 4294967295)
         AND _role_vote_lock_key(@role) <> _role_vote_lock_key('migrator')
      """, cancellationToken, ("role", ROLE));

    await Assert.That(sameAsTableOid).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task Status_ReportsHeldLapsedAndVacant_WithEpochAndElectionCountAsync(CancellationToken cancellationToken) {
    // Requirements 9 and 10: "no holder" is visible, and so are holder, epoch, renewal and count.
    var a = await _joinAsync(cancellationToken);
    var vote = await _electAsync(a, cancellationToken);

    async Task<AssignmentStatus> statusAsync() {
      await using var conn = await _openAsync(cancellationToken);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT state, holder_instance_id, epoch, election_count, last_vacated_reason, renewed_at "
        + "FROM wh_role_assignment_status() WHERE role = @role";
      cmd.Parameters.AddWithValue("role", ROLE);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      await Assert.That(await reader.ReadAsync(cancellationToken)).IsTrue();
      return new AssignmentStatus(reader.GetString(0), await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetGuid(1),
        reader.GetInt64(2), reader.GetInt64(3), await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4));
    }

    await _oweAsync("Rewrite", cancellationToken);
    await using (var pendingConn = await _openAsync(cancellationToken)) {
      await using var pending = pendingConn.CreateCommand();
      pending.CommandText = "SELECT pending_work FROM wh_role_assignment_status() WHERE role = @role";
      pending.Parameters.AddWithValue("role", ROLE);
      await Assert.That((long)(await pending.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(1L)
        .Because("the status surface says how much duty work is waiting for the holder");
    }

    var held = await statusAsync();
    await Assert.That(held.State).IsEqualTo("held");
    await Assert.That(held.Holder).IsEqualTo(a);
    await Assert.That(held.Epoch).IsEqualTo(vote.Epoch);
    await Assert.That(held.Elections).IsEqualTo(1L);

    await _ageAsync(_lease + TimeSpan.FromSeconds(1), cancellationToken);
    await Assert.That((await statusAsync()).State).IsEqualTo("lapsed")
      .Because("a holder whose lease ran out holds nothing, even before the next vote records it");

    await _ageAsync(-(_lease + TimeSpan.FromSeconds(1)), cancellationToken);
    await _releaseAsync(a, vote.Epoch, cancellationToken);
    var vacant = await statusAsync();
    await Assert.That(vacant.State).IsEqualTo("vacant");
    await Assert.That(vacant.LastReason).IsEqualTo("released");
    await Assert.That(vacant.Epoch).IsEqualTo(vote.Epoch)
      .Because("vacating keeps the epoch, so the next assignment is always epoch + 1");
  }

  private sealed record AssignmentStatus(string State, Guid? Holder, long Epoch, long Elections, string? LastReason);
}
