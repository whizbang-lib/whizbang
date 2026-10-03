using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The #968 decisions through the real elector: each instance presents its library version, so a
/// vacant role goes to the newest one and a live older holder is asked to drain; a holder marks the
/// backend running its duty's statement; a bridged would-be winner ends a lapsed holder's lock
/// session; several roles are voted for in one statement; and an unbridged holder steps aside for an
/// old session-lock holder. Database time moves by shifting stored instants back, and the renew
/// throttle runs on a fake clock, so nothing sleeps.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RoleAssignmentResilienceE2ETests : EFCoreTestBase {
  private const string ROLE = StartupDuties.MAINTAINER;
  private const string OTHER = "commit-stamper";
  private static readonly RoleAssignmentOptions _defaults = new();

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "role-svc";
    public string HostName => "role-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private WhizbangNotificationOptions _notification(string? connectionString = null) => new() { DirectConnectionString = connectionString ?? ConnectionString };

  private static IConfiguration _config() => new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private PgDutyElector _legacyFor(Pod pod) => new(Options.Create(_notification()), _config(), pod, NullLogger<PgDutyElector>.Instance);

  private PgRoleElector _electorFor(
      Pod pod, string? version = null, bool bridge = false, TimeProvider? time = null, RoleAssignmentMetrics? metrics = null,
      Action<RoleAssignmentOptions>? configure = null, string? connectionString = null) {
    var options = new RoleAssignmentOptions { HoldLegacySessionLock = bridge };
    options.Roles.Add(OTHER);
    configure?.Invoke(options);
    return new PgRoleElector(
      Options.Create(_notification(connectionString)), Options.Create(options), _config(), pod, _legacyFor(pod),
      NullLogger<PgRoleElector>.Instance, version is null ? null : new LibraryVersionProvider(version),
      timeProvider: time, metrics: metrics);
  }

  private async Task<Pod> _joinAsync(CancellationToken ct) {
    var pod = new Pod();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
    return pod;
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task<object?> _scalarAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = await _openAsync(ct);
    return await _scalarOnAsync(conn, sql, ct, args);
  }

  private static async Task<object?> _scalarOnAsync(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync(ct);
    return result is DBNull ? null : result;
  }

  private Task _ageAsync(string role, TimeSpan by, CancellationToken ct) => _scalarAsync("""
    WITH a AS (
      UPDATE wh_role_assignments
         SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
             lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
       WHERE role = @role RETURNING 1)
    UPDATE wh_role_candidates SET last_voted_at = last_voted_at - @by WHERE role = @role
    """, ct, ("by", by), ("role", role));

  private sealed class MeterReader : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, long Value)> _seen = [];
    public MeterReader(Meter meter) {
      _listener.InstrumentPublished = (instrument, l) => {
        if (instrument.Meter == meter) {
          l.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => _seen.Add((instrument.Name, value)));
      _listener.Start();
    }
    public long Total(string name) {
      _seen.Clear();
      _listener.RecordObservableInstruments();
      return _seen.Where(s => s.Name == name).Sum(s => s.Value);
    }
    public void Dispose() => _listener.Dispose();
  }

  private sealed class OtherGrant : IDutyGrant {
    public string Duty => ROLE;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  [Test]
  [Timeout(120000)]
  public async Task ANewerInstance_AsksTheOlderHolderToDrain_WhichLearnsItOnItsNextRenewalAsync(CancellationToken cancellationToken) {
    // Decision 2 of #968.
    var time = new FakeTimeProvider();
    var older = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(older, "0.2606.0", time: time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await Assert.That(grant.DrainRequested).IsFalse();

    var asked = await _electorFor(newer, "0.2607.0").TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(asked.Refusal).IsEqualTo(DutyRefusal.Contended);
    await Assert.That(asked.Detail).Contains("asked to drain");
    await Assert.That(grant.DrainRequested).IsFalse().Because("the holder learns it on a renewal, not before");

    time.Advance(_defaults.RenewInterval);
    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsTrue()
      .Because("a drain request renews: the holder finishes its step first");
    await Assert.That(grant.DrainRequested).IsTrue();
    time.Advance(_defaults.RenewInterval);
    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    await Assert.That(grant.DrainRequested).IsTrue().Because("still asked; it stays asked until the release");

    await grant.DisposeAsync();
    var handedOver = await _electorFor(newer, "0.2607.0").TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(handedOver.Grant).IsNotNull();
    await Assert.That(handedOver.Grant!.Epoch).IsEqualTo(grant.Epoch + 1);
    await handedOver.Grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task AVacantRole_GoesToTheNewestVersionVotingForItAsync(CancellationToken cancellationToken) {
    // Decision 3 of #968.
    var holder = await _joinAsync(cancellationToken);
    var older = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(holder, "0.2606.0").TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    _ = await _electorFor(newer, "0.2607.0-alpha.3").TryAcquireAsync(ROLE, cancellationToken);
    await grant.DisposeAsync();

    var deferred = await _electorFor(older, "0.2606.0").TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(deferred.Refusal).IsEqualTo(DutyRefusal.Contended);
    await Assert.That(deferred.Detail).Contains("newer library version");

    var won = await _electorFor(newer, "0.2607.0-alpha.3").TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(won.Grant).IsNotNull();
    await won.Grant!.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task MarkDutyBackend_KeepsTheRoleThroughAStatementThatOutlastsTheLeaseAsync(CancellationToken cancellationToken) {
    // Decision 1 of #968: the backstop. The checks run on the marked backend, which is therefore the
    // backend running a statement; see RoleAssignmentResilienceSqlTests for the vote's side.
    var a = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(a).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await using var duty = await _openAsync(cancellationToken);

    var mark = await PgRoleElector.MarkDutyBackendAsync(duty, grant, cancellationToken);
    await _ageAsync(ROLE, _defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    var whileRunning = (string)(await _scalarOnAsync(duty, "SELECT state FROM wh_role_assignment_status() WHERE role = @role",
      cancellationToken, ("role", ROLE)))!;
    await Assert.That(whileRunning).IsEqualTo("held").Because("the marked backend is running a statement");
    await PgRoleElector.AssertEpochAsync(duty, grant, cancellationToken);

    await mark.DisposeAsync();
    var afterward = (string)(await _scalarOnAsync(duty, "SELECT state FROM wh_role_assignment_status() WHERE role = @role",
      cancellationToken, ("role", ROLE)))!;
    await Assert.That(afterward).IsEqualTo("lapsed").Because("the mark is cleared when the statement's scope ends");
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task MarkDutyBackend_MarksNothing_ForAGrantThatIsNotARole_OrIsNoLongerHeldAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    await using var duty = await _openAsync(cancellationToken);

    await using (await PgRoleElector.MarkDutyBackendAsync(duty, new OtherGrant(), cancellationToken)) {
      await PgRoleElector.AssertEpochAsync(duty, new OtherGrant(), cancellationToken);
    }
    var nothingMarked = await PgRoleElector.MarkDutyBackendAsync(duty, null, cancellationToken);
    await nothingMarked.DisposeAsync();

    var stale = (await _electorFor(a).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await _ageAsync(ROLE, _defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    var current = (await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await using (await PgRoleElector.MarkDutyBackendAsync(duty, stale, cancellationToken)) {
      await Assert.That(await _scalarAsync("SELECT duty_backend_pid FROM wh_role_assignments WHERE role = @role",
        cancellationToken, ("role", ROLE))).IsNull().Because("a superseded grant cannot mark its successor's assignment");
    }
    var error = await Assert.That(async () => await PgRoleElector.AssertEpochAsync(duty, stale, cancellationToken)).Throws<PostgresException>();
    await Assert.That(error!.SqlState).IsEqualTo("WHF01");
    await Assert.That(async () => await PgRoleElector.MarkDutyBackendAsync(null!, stale, cancellationToken)).Throws<ArgumentNullException>();
    await Assert.That(async () => await PgRoleElector.AssertEpochAsync(null!, stale, cancellationToken)).Throws<ArgumentNullException>();
    await Assert.That(async () => await PgRoleElector.AssertEpochAsync(duty, null!, cancellationToken)).Throws<ArgumentNullException>();
    await current.DisposeAsync();
    await stale.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task ClearingAMark_OnAConnectionThatDied_DoesNotThrowAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(a).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var duty = await _openAsync(cancellationToken);
    var mark = await PgRoleElector.MarkDutyBackendAsync(duty, grant, cancellationToken);
    await duty.DisposeAsync();

    await Assert.That(async () => await mark.DisposeAsync()).ThrowsNothing()
      .Because("a dead backend is never active, so there is nothing left to clear");
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task ABridgedVoter_EndsALapsedBridgedHoldersLockSession_AndWinsAsync(CancellationToken cancellationToken) {
    // Decision 4 of #968: logged and counted.
    await using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new RoleAssignmentMetrics(new WhizbangMetrics(provider.GetRequiredService<IMeterFactory>()));
    using var reader = new MeterReader(metrics.Elections.Meter);
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var stuck = (await _electorFor(a, bridge: true).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var whileLive = await _electorFor(b, bridge: true, metrics: metrics).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(whileLive.Refusal).IsEqualTo(DutyRefusal.Contended).Because("a live bridged holder's session is never ended");

    await _ageAsync(ROLE, _defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    var takeover = await _electorFor(b, bridge: true, metrics: metrics).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(takeover.Grant).IsNotNull()
      .Because($"the stuck holder's session was ended, so the lock and the vote are free. Refusal: {takeover.Refusal} — {takeover.Detail}");
    await Assert.That(takeover.Grant!.Epoch).IsEqualTo(stuck.Epoch + 1);
    await Assert.That(reader.Total("whizbang.roles.bridge_sessions_ended")).IsEqualTo(1);
    await Assert.That(await stuck.VerifyStillHeldAsync(cancellationToken)).IsFalse()
      .Because("the stuck holder's bridge session is gone, so it knows it lost the role");
    await takeover.Grant.DisposeAsync();
    await stuck.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task AnUnbridgedHolder_StepsAsideWithinOneRenewal_WhenASessionLockHolderTookTheDutyAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var old = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(a, time: time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    // An instance on the session-lock elector starting after the vote: unsafe, and what the bridge prevents.
    await using var legacy = (await _legacyFor(old).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    time.Advance(_defaults.RenewInterval);

    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsFalse();
    await Assert.That((string)(await _scalarAsync("SELECT last_vacated_reason FROM wh_role_assignments WHERE role = @role",
      cancellationToken, ("role", ROLE)))!).IsEqualTo("legacy_holder");
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task TryAcquireMany_VotesForEveryUnheldRoleInOneStatement_AndAsksTheRestOneByOneAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var elector = _electorFor(a, "0.2607.0", configure: o => o.RoleLeases[OTHER] = TimeSpan.FromMinutes(1));

    var first = await elector.TryAcquireManyAsync([ROLE, OTHER, "not-a-role"], cancellationToken);

    await Assert.That(first[0].Grant).IsNotNull();
    await Assert.That(first[1].Grant).IsNotNull();
    await Assert.That(first[2].Grant).IsNotNull().Because("a duty this elector does not manage is delegated");
    await Assert.That(first[2].Grant!.Epoch).IsNull();
    var lease = (TimeSpan)(await _scalarAsync("SELECT lease FROM wh_role_assignments WHERE role = @role", cancellationToken, ("role", OTHER)))!;
    await Assert.That(lease).IsEqualTo(TimeSpan.FromMinutes(1)).Because("each role is granted its own declared lease");

    var again = await elector.TryAcquireManyAsync([ROLE, OTHER], cancellationToken);
    await Assert.That(again[0].Grant!.Epoch).IsEqualTo(first[0].Grant!.Epoch).Because("held roles are answered from their tenure");
    foreach (var attempt in first.Concat(again)) {
      await attempt.Grant!.DisposeAsync();
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task TryAcquireMany_WhenBridged_OrWithoutAConnection_AsksOneByOneAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var bridged = await _electorFor(a, bridge: true).TryAcquireManyAsync([ROLE, OTHER], cancellationToken);
    await Assert.That(bridged.All(x => x.Grant is not null)).IsTrue();
    foreach (var attempt in bridged) {
      await attempt.Grant!.DisposeAsync();
    }

    var noConnection = new PgRoleElector(
      Options.Create(new WhizbangNotificationOptions()), Options.Create(new RoleAssignmentOptions()), _config(), a,
      NullDutyElector.Instance, NullLogger<PgRoleElector>.Instance, null);
    var unavailable = await noConnection.TryAcquireManyAsync([ROLE, StartupDuties.MIGRATOR], cancellationToken);
    await Assert.That(unavailable.All(x => x.Refusal == DutyRefusal.Unavailable)).IsTrue();
    await Assert.That(async () => await noConnection.TryAcquireManyAsync(null!, cancellationToken)).Throws<ArgumentNullException>();
    await Assert.That(noConnection.Manages(ROLE)).IsTrue();
    await Assert.That(noConnection.RenewInterval).IsEqualTo(_defaults.RenewInterval);
  }

  [Test]
  [Timeout(120000)]
  public async Task ReadAssignments_ReportsADrainRequest_AndTheBackstopAsync(CancellationToken cancellationToken) {
    var older = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var elector = _electorFor(older, "0.2606.0");
    var grant = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    _ = await _electorFor(newer, "0.2607.0").TryAcquireAsync(ROLE, cancellationToken);

    var snapshot = (await elector.ReadAssignmentsAsync(cancellationToken)).Single(s => s.Role == ROLE);

    await Assert.That(snapshot.DrainRequestedAt).IsNotNull();
    await Assert.That(snapshot.DutyBackendActive).IsFalse();
    await grant.DisposeAsync();
  }

  [Test]
  public async Task LibraryVersionKey_OrdersVersionsAsSemVerDoesAsync() {
    string[] ordered = ["0.9.0", "0.2606.0-alpha", "0.2606.0-alpha.2", "0.2606.0-alpha.12", "0.2606.0-alpha.beta",
      "0.2606.0-beta", "0.2606.0-rc.1", "0.2606.0", "0.2606.1", "1.0"];
    for (var i = 1; i < ordered.Length; i++) {
      var lower = LibraryVersionKey.From(ordered[i - 1]);
      var higher = LibraryVersionKey.From(ordered[i]);
      await Assert.That(_compare(lower, higher)).IsLessThan(0).Because($"{ordered[i - 1]} precedes {ordered[i]}");
    }
    await Assert.That(LibraryVersionKey.From("0.2606.0+abc123")).IsEquivalentTo(LibraryVersionKey.From("0.2606.0"));
    await Assert.That(LibraryVersionKey.From(null)).IsEmpty();
    await Assert.That(LibraryVersionKey.From(" ")).IsEmpty();
    await Assert.That(LibraryVersionKey.From("not.a.version")).IsEmpty();
  }

  /// <summary>Array order as Postgres compares integer arrays: element by element, then the shorter first.</summary>
  private static int _compare(int[] left, int[] right) {
    for (var i = 0; i < Math.Min(left.Length, right.Length); i++) {
      if (left[i] != right[i]) {
        return left[i].CompareTo(right[i]);
      }
    }
    return left.Length.CompareTo(right.Length);
  }

  [Test]
  [Timeout(120000)]
  public async Task ABridgedNewerInstance_StillAsksTheOlderHolderToDrainAsync(CancellationToken cancellationToken) {
    // The bridge is on by default; losing the legacy lock must not cost the drain or the candidacy.
    var time = new FakeTimeProvider();
    var older = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(older, "0.2606.0", bridge: true, time: time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    var asked = await _electorFor(newer, "0.2607.0", bridge: true).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(asked.Refusal).IsEqualTo(DutyRefusal.Contended);
    await Assert.That(asked.Detail).Contains("asked to drain");
    var again = await _electorFor(newer, "0.2607.0", bridge: true).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(again.Detail).Contains("asked to drain").Because("the drain stays pending");

    time.Advance(_defaults.RenewInterval);
    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    await Assert.That(grant.DrainRequested).IsTrue();
    await grant.DisposeAsync();

    var handedOver = await _electorFor(newer, "0.2607.0", bridge: true).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(handedOver.Grant).IsNotNull();
    await handedOver.Grant!.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task ABridgedLoser_ToASessionLockHolder_IsPlainlyContended_AndAnEqualVersionAsksNoDrainAsync(CancellationToken cancellationToken) {
    var old = await _joinAsync(cancellationToken);
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    await using (var legacy = (await _legacyFor(old).TryAcquireAsync(ROLE, cancellationToken)).Grant!) {
      var attempt = await _electorFor(a, "0.2607.0", bridge: true).TryAcquireAsync(ROLE, cancellationToken);
      await Assert.That(attempt.Detail).Contains("session lock").Because("there is no assignment to drain");
    }
    var held = (await _electorFor(a, "0.2607.0", bridge: true).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var equal = await _electorFor(b, "0.2607.0", bridge: true).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(equal.Detail).Contains("session lock");
    await Assert.That(await _scalarAsync("SELECT drain_requested_at FROM wh_role_assignments WHERE role = @role",
      cancellationToken, ("role", ROLE))).IsNull();
    await held.DisposeAsync();
  }
}
