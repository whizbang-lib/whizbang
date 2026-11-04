// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #2027 gravity: instances coordinate through one standard mechanism. Raw notification channels, advisory locks and
/// liveness checks may appear only in the files and SQL functions allowlisted here, each with the reason it is there. A
/// new one anywhere else fails this test, so the next author cannot quietly grow a fifth definition of "alive" or a
/// third way to hold a duty.
/// </summary>
/// <remarks>
/// <para>
/// The C# scan reads every source file under <c>src</c> with comments removed. The SQL scan reads the migration corpus
/// and judges only each function's LAST definition (the one a database runs), with SQL comments removed.
/// </para>
/// <para>
/// The allowlists only shrink: an entry that no longer matches fails <c>EveryAllowlistEntry_StillMatchesAsync</c>.
/// Entries marked <c>PENDING</c> are what slice 1 of #2022 has not folded in yet (the raw channels); each names the part
/// of #2027 that removes it. No duty is held by an advisory lock (#2044): what remains allowlisted is mutual exclusion
/// around one statement or one DDL pass, never a duty.
/// </para>
/// </remarks>
/// <docs>contributors/gravity</docs>
[Category("Startup")]
public partial class CoordinationSourceGuardTests {
  private const string PENDING_CHANNELS = "PENDING #2027 part 4: a raw notification channel, to move onto the signal bus";
  private const string EXCLUSION = "a transaction-scoped advisory lock: mutual exclusion for one statement or DDL pass, released at commit; not a duty";
  private const string CONCURRENT_DROP = "the schema lock held at session level for the three statements of one DROP INDEX CONCURRENTLY, which "
    + "cannot run in a transaction, and only on a connection that reaches one backend: mutual exclusion with the schema "
    + "pass around one statement, not a duty (#2044)";

  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>C# files that may name a notification channel.</summary>
  private static readonly Dictionary<string, string> _notifyAllowlist = new(StringComparer.Ordinal) {
    ["src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs"] = "the one LISTEN connection per instance: the signal bus's transport underneath",
    ["src/Whizbang.Data.Postgres/Notifications/PostgresSignalTransport.cs"] = "the signal bus's Postgres transport",
    ["src/Whizbang.Data.Postgres/Notifications/PgAppSignalChannel.cs"] = PENDING_CHANNELS,
  };

  /// <summary>C# files that may take or release an advisory lock.</summary>
  private static readonly Dictionary<string, string> _advisoryAllowlist = new(StringComparer.Ordinal) {
    ["src/Whizbang.Data.Postgres/AdvisoryLockRelease.cs"] = "releases " + CONCURRENT_DROP + ", and logs a release a pooler sent elsewhere",
    ["src/Whizbang.Data.Postgres/Schema/ManagedSchemaReconciler.cs"] = EXCLUSION + "; and " + CONCURRENT_DROP,
    ["src/Whizbang.Data.Postgres/SchemaInitializationLock.cs"] = EXCLUSION,
    ["src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs"] = EXCLUSION,
    ["src/Whizbang.Data.Postgres/CanonicalTemporalRewritePhase.cs"] = EXCLUSION,
    ["src/Whizbang.Data.Postgres/PostgresCapabilityProvisioner.cs"] = EXCLUSION,
    ["src/Whizbang.Data.EFCore.Postgres/SchemaStepRunner.cs"] = EXCLUSION,
    ["src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveAdapter.cs"] = EXCLUSION,
    ["src/Whizbang.Data.Dapper.Postgres/Collective/DapperCollectiveEventApplier.cs"] = EXCLUSION,
    ["src/Whizbang.Archive.AzureBlob/AzureBlobArchiveStore.cs"] = EXCLUSION,
  };

  /// <summary>C# files that may compare a heartbeat's age or read session activity.</summary>
  private static readonly Dictionary<string, string> _livenessAllowlist = new(StringComparer.Ordinal) {
    ["src/Whizbang.Core/Startup/StartupStatusReport.cs"] = "shows how long ago an instance beat; it judges nothing",
    ["src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs"] = "a write throttle on the beat itself; it judges no peer",
    ["src/Whizbang.Core/Workers/InboxHandlerWorker.cs"] = "a diagnostic message names the system view an operator should read; nothing is judged",
    ["src/Whizbang.Data.Postgres/SupersededRowVersionFence.cs"] = "a visibility fence over in-flight transaction ids; it judges no instance",
    ["src/Whizbang.Data.Postgres/AdvisoryLockProbe.cs"] = "reads whether the transaction-scoped schema lock is held elsewhere, so a "
      + "contender waits for the session doing schema work instead of queueing behind it: the state of an exclusion lock, which "
      + "judges no instance and holds no duty (#2044)",
  };

  /// <summary>SQL functions (by their last definition) that may judge liveness or read session activity.</summary>
  private static readonly Dictionary<string, string> _sqlLivenessAllowlist = new(StringComparer.Ordinal) {
    ["wh_instance_liveness"] = "THE liveness rule",
    ["wh_direct_alive_lock_holders"] = "the lock half of the rule",
    ["register_instance_heartbeat"] = "a write throttle on the beat itself; it judges no peer",
    ["_role_backend_active"] = "whether a role holder's own long statement is still running: a lease extension, not a liveness verdict",
    ["wh_mark_role_duty_backend"] = "marks the backend running a role's statement: a lease extension, not a liveness verdict",
    ["stamp_pending_commit_sequences"] = "the visibility fence reads in-flight transaction ids; it judges no instance",
  };

  /// <summary>SQL functions (by their last definition) that may take a SESSION advisory lock.</summary>
  private static readonly Dictionary<string, string> _sqlSessionLockAllowlist = new(StringComparer.Ordinal) {
    ["claim_instance_alive_lock"] = "the alive-lock: the lock half of the liveness rule, held by the instance's own LISTEN session",
  };

  /// <summary>SQL functions (by their last definition) that may notify a channel.</summary>
  private static readonly Dictionary<string, string> _sqlNotifyAllowlist = new(StringComparer.Ordinal) {
    ["ring_doorbells"] = "the doorbell ring: the work-available wake the claim depends on; " + PENDING_CHANNELS,
    ["cleanup_stale_instances"] = PENDING_CHANNELS + " (the orphan wake after a reap)",
    ["wh_publish_partition_assignment"] = PENDING_CHANNELS + " (the partition assignment)",
    ["wh_record_multi_stream_run"] = PENDING_CHANNELS + " (multi-stream commands)",
    ["wh_release_role"] = PENDING_CHANNELS + " (a released role)",
  };

  private const string MAINTENANCE_STEP = "a maintenance step: the maintenance cycle runs it only after the maintainer turn, which a "
    + "closed run permit denies (#2045)";
  private const string STORE = "the store that implements the claim; its callers obey the run permit";

  /// <summary>
  /// C# files that take work for this instance without a hold of their own, each with the reason (#2045). Everything
  /// else that claims takes a hold (<c>RunPermit.EnterAsync</c>) or asks the maintainer turn.
  /// </summary>
  private static readonly Dictionary<string, string> _claimAllowlist = new(StringComparer.Ordinal) {
    ["src/Whizbang.Core/Messaging/IWorkCoordinator.cs"] = "declares the claims",
    ["src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs"] = STORE,
    ["src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.RowRetention.cs"] = STORE,
    ["src/Whizbang.Data.Postgres/FleetClaim.cs"] = "the once-per-window claim helper the maintenance steps share",
    ["src/Whizbang.Data.Postgres/Observability/PostgresTableStatisticsProvider.cs"] = "the physical-column fill's window claim, "
      + "run by its maintenance step",
    ["src/Whizbang.Core/Workers/QueueIndexReindexStep.cs"] = MAINTENANCE_STEP,
    ["src/Whizbang.Data.Dapper.Postgres/DapperManagedSchemaReconcileStep.cs"] = MAINTENANCE_STEP,
    ["src/Whizbang.Data.Dapper.Postgres/DapperPhysicalColumnFillMaintenanceStep.cs"] = MAINTENANCE_STEP,
    ["src/Whizbang.Data.EFCore.Postgres/IndexStatisticsMaintenanceStep.cs"] = MAINTENANCE_STEP,
    ["src/Whizbang.Data.EFCore.Postgres/ManagedSchemaReconcileStep.cs"] = MAINTENANCE_STEP,
    ["src/Whizbang.Data.EFCore.Postgres/PhysicalColumnFillMaintenanceStep.cs"] = MAINTENANCE_STEP,
    ["src/Whizbang.Core/Dispatcher.cs"] = "claims an emission key while handling a message it already holds: deduplication, not work taken",
    ["src/Whizbang.Core/Fingerprint/TypeDefinitionReconciler.cs"] = "a startup reconcile, once per window, before the instance takes work",
    ["src/Whizbang.Testing/Workers/WorkCoordinatorPumpAdapter.cs"] = "a test harness's manual pump, never hosted",
  };

  /// <summary>C# files that may write a halting phase (#2045): the path, and the stand-down's report in a host without it.</summary>
  private static readonly Dictionary<string, string> _haltPhaseAllowlist = new(StringComparer.Ordinal) {
    ["src/Whizbang.Core/RunControl/WhizbangRunController.cs"] = "THE run-control path",
    ["src/Whizbang.Core/Startup/StartupStandDown.cs"] = "records StandingBy itself only in a host with no run-control path, "
      + "where there is no permit, no held work and no role to release",
  };

  [GeneratedRegex(@"\.(?:Try)?(?:Claim|Lease)[A-Za-z]*Async\(|\.ClaimAsync\(|FetchPendingCoalesceAsync\(|IntegrityClaim[A-Za-z]*Async\(")]
  private static partial Regex _workClaim();

  [GeneratedRegex(@"RunPermit\.EnterAsync\(|MaintainerDuty\.IsThisInstancesTurn\(")]
  private static partial Regex _obeysThePermit();

  [GeneratedRegex(@"(?:AdvanceToAsync|EnterPhaseAsync)\([^;]*LifecyclePhase\.(?:Pausing|Paused|StandingBy|Stopping|Stopped|Resuming)\b")]
  private static partial Regex _haltPhase();

  [Test]
  public async Task EveryClaim_ObeysTheRunPermitAsync() {
    // #2045: a claim is taken inside a hold of the one run permit, or behind the maintainer turn that a closed permit
    // denies. A new claim anywhere else fails here, so it cannot keep taking work through a stand-down, a pause or a stop.
    var offenders = _csharpSources()
      .Where(f => !_claimAllowlist.ContainsKey(f.Key) && _workClaim().IsMatch(f.Value) && !_obeysThePermit().IsMatch(f.Value))
      .Select(f => f.Key)
      .ToList();
    await Assert.That(offenders).IsEmpty()
      .Because("a claim that does not obey the run permit; take a hold (RunPermit.EnterAsync) or ask "
        + "MaintainerDuty.IsThisInstancesTurn: " + string.Join(", ", offenders));
  }

  [GeneratedRegex(@":\s*BackgroundService\b")]
  private static partial Regex _hostedWorker();

  [GeneratedRegex(@"\.SubscribeBatchAsync\(|SubscribeWithRetryAsync\(")]
  private static partial Regex _subscribes();

  [Test]
  public async Task EveryHostedConsumer_ObeysTheRunPermitAsync() {
    // #2045: a hosted worker that subscribes to a transport is called back rather than looping, so it obeys the run
    // permit by acting on each change (RunPermit.Obey). A consumer that subscribes without it keeps receiving through a
    // stand-down, a pause and a stop, whoever registers it.
    var offenders = _csharpSources()
      .Where(f => _hostedWorker().IsMatch(f.Value) && _subscribes().IsMatch(f.Value) && !f.Value.Contains("RunPermit.Obey(", StringComparison.Ordinal))
      .Select(f => f.Key)
      .ToList();
    await Assert.That(offenders).IsEmpty()
      .Because("a hosted consumer that does not obey the run permit: " + string.Join(", ", offenders));
    var consumers = _csharpSources().Where(f => _hostedWorker().IsMatch(f.Value) && _subscribes().IsMatch(f.Value))
      .Select(f => f.Key).Order(StringComparer.Ordinal).ToList();
    await Assert.That(consumers).IsEquivalentTo([
      "src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs",
      "src/Whizbang.Core/Workers/TransportConsumerWorker.cs",
    ]).Because("the scan must see the consumers it vouches for, or it vouches for nothing");
  }

  [Test]
  public async Task HaltingPhases_AreWrittenOnlyByTheRunControlPathAsync() {
    // #2045: Pausing, Paused, StandingBy, Stopping, Stopped and Resuming are reported by the one path, after the permit
    // has closed or reopened. A phase written anywhere else would say "paused" while the workers still claim.
    await _assertOnlyAllowedAsync(_haltPhase(), _haltPhaseAllowlist,
      "a halting phase written outside the run-control path; take IWhizbangRunController.HaltAsync or ResumeAsync instead");
  }

  [Test]
  public async Task TheRunControlScans_SeeWhatTheyGuardAsync() {
    var csharp = _csharpSources();

    await Assert.That(_workClaim().IsMatch(csharp["src/Whizbang.Core/Workers/ClaimWorker.cs"])).IsTrue();
    await Assert.That(_obeysThePermit().IsMatch(csharp["src/Whizbang.Core/Workers/ClaimWorker.cs"])).IsTrue();
    await Assert.That(_obeysThePermit().IsMatch(csharp["src/Whizbang.Core/Workers/IntegrityAuditWorker.cs"])).IsTrue();
    await Assert.That(_haltPhase().IsMatch(csharp["src/Whizbang.Core/RunControl/WhizbangRunController.cs"])).IsTrue();
    await Assert.That(_haltPhase().IsMatch("await _lifecycle.AdvanceToAsync(LifecyclePhase.StandingBy, ct);")).IsTrue();
    var stale = _claimAllowlist.Keys.Where(path => !csharp.TryGetValue(path, out var code) || !_workClaim().IsMatch(code))
      .Concat(_haltPhaseAllowlist.Keys.Where(path => !csharp.TryGetValue(path, out var code) || !_haltPhase().IsMatch(code)))
      .ToList();
    await Assert.That(stale).IsEmpty().Because("an allowlist only shrinks: " + string.Join(", ", stale));
  }

  [GeneratedRegex(@"(?:^|[""\s;(])(?:UN)?LISTEN\s+[\\""{]|\bNOTIFY\s+[\\""{]|pg_notify\s*\(")]
  private static partial Regex _notify();

  [GeneratedRegex(@"pg_(?:try_)?advisory_(?:xact_)?lock(?:_shared)?\s*\(|pg_advisory_unlock(?:_all)?\s*\(")]
  private static partial Regex _advisory();

  [GeneratedRegex(@"pg_(?:try_)?advisory_lock(?:_shared)?\s*\(")]
  private static partial Regex _sessionAdvisory();

  [GeneratedRegex(@"last_heartbeat_at\s*[<>]|[<>]=?\s*(?:\w+\.)?last_heartbeat_at|LastHeartbeatAt\s*[<>]|-\s*(?:\w+\.)*LastHeartbeatAt|is_instance_alive\s*\(|pg_stat_activity|pg_locks")]
  private static partial Regex _liveness();

  [GeneratedRegex(@"CREATE OR REPLACE FUNCTION\s+__SCHEMA__\.(\w+)\s*\(.*?\$\$(.*?)\$\$", RegexOptions.Singleline)]
  private static partial Regex _sqlFunction();

  [GeneratedRegex(@"DROP FUNCTION IF EXISTS\s+__SCHEMA__\.(\w+)\s*\(|drop_all_overloads\('(\w+)'\)")]
  private static partial Regex _sqlFunctionDrop();

  [GeneratedRegex(@"--[^\n]*")]
  private static partial Regex _sqlComment();

  [GeneratedRegex(@"^\s*(?://|\*|/\*).*$|(?<=;|\{|\)|,)\s*//.*$", RegexOptions.Multiline)]
  private static partial Regex _csComment();

  [Test]
  public async Task NotifyAndListen_AppearOnlyInTheCoordinationFilesAsync() =>
    await _assertOnlyAllowedAsync(_notify(), _notifyAllowlist, "a raw notification channel; publish on the signal bus instead");

  [Test]
  public async Task AdvisoryLocks_AppearOnlyInTheAllowlistedFilesAsync() =>
    await _assertOnlyAllowedAsync(_advisory(), _advisoryAllowlist, "an advisory lock; hold a duty as a role assignment instead");

  [Test]
  public async Task LivenessChecks_AppearOnlyInTheAllowlistedFilesAsync() =>
    await _assertOnlyAllowedAsync(_liveness(), _livenessAllowlist,
      "a liveness check; ask the one rule (wh_instance_is_live, wh_live_instance_ids) instead");

  [Test]
  public async Task SqlLivenessChecks_AppearOnlyInTheRuleAndTheAllowlistedFunctionsAsync() =>
    await _assertSqlOnlyAllowedAsync(_liveness(), _sqlLivenessAllowlist, "judges liveness without the one rule (wh_instance_liveness)");

  [Test]
  public async Task SqlSessionAdvisoryLocks_AppearOnlyInTheAllowlistedFunctionsAsync() =>
    await _assertSqlOnlyAllowedAsync(_sessionAdvisory(), _sqlSessionLockAllowlist, "takes a session advisory lock: a duty held outside the role table");

  [Test]
  public async Task SqlNotifications_AppearOnlyInTheAllowlistedFunctionsAsync() =>
    await _assertSqlOnlyAllowedAsync(_notify(), _sqlNotifyAllowlist, "notifies a raw channel");

  [Test]
  public async Task EveryAllowlistEntry_StillMatchesAsync() {
    var csharp = _csharpSources();
    var sql = _latestSqlFunctions();
    var stale = new List<string>();
    foreach (var (allowlist, pattern) in new[] { (_notifyAllowlist, _notify()), (_advisoryAllowlist, _advisory()), (_livenessAllowlist, _liveness()) }) {
      stale.AddRange(allowlist.Keys.Where(path => !csharp.TryGetValue(path, out var code) || !pattern.IsMatch(code)));
    }
    foreach (var (allowlist, pattern) in new[] { (_sqlLivenessAllowlist, _liveness()), (_sqlSessionLockAllowlist, _sessionAdvisory()), (_sqlNotifyAllowlist, _notify()) }) {
      stale.AddRange(allowlist.Keys.Where(name => !sql.TryGetValue(name, out var body) || !pattern.IsMatch(body)));
    }

    await Assert.That(stale).IsEmpty()
      .Because("an allowlist only shrinks: remove an entry once its file or function no longer needs it: " + string.Join(", ", stale));
  }

  [Test]
  public async Task NoDuty_IsHeldByAnAdvisoryLockAnywhereAsync() {
    // #2044: one way to hold a duty, the role table. The duty electors' files take no advisory lock, and no allowlist
    // entry is left waiting for the advisory-lock duties to move.
    var csharp = _csharpSources();
    string[] dutyHolders = [
      "src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs",
      "src/Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs",
      "src/Whizbang.Data.Postgres/MigratorWatch.cs",
    ];
    foreach (var path in dutyHolders) {
      await Assert.That(_advisory().IsMatch(csharp[path])).IsFalse().Because($"{path} holds a duty; a duty is a role assignment");
    }
    await Assert.That(csharp["src/Whizbang.Data.Postgres/MigratorWatch.cs"].Contains("AdvisoryLockProbe", StringComparison.Ordinal)).IsFalse()
      .Because("a waiter watches the migrator's assignment and recorded holdings, never a duty lock");
    await Assert.That(csharp.Keys.Any(p => p.EndsWith("/PgDutyElector.cs", StringComparison.Ordinal))).IsFalse()
      .Because("the session-lock duty elector is gone; every duty is voted in the role table");
    var reasons = _advisoryAllowlist.Values.Concat(_livenessAllowlist.Values).Concat(_sqlLivenessAllowlist.Values)
      .Concat(_sqlSessionLockAllowlist.Values);
    await Assert.That(reasons.Where(r => r.Contains("#2027 part 2", StringComparison.Ordinal))).IsEmpty();
  }

  [Test]
  public async Task TheScans_SeeTheMechanismsTheyGuardAsync() {
    // A guard that reads nothing passes everything: prove each scan finds what it exists to find.
    var csharp = _csharpSources();
    var sql = _latestSqlFunctions();

    await Assert.That(_notify().IsMatch(csharp["src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs"])).IsTrue();
    await Assert.That(_advisory().IsMatch(csharp["src/Whizbang.Data.Postgres/SchemaInitializationLock.cs"])).IsTrue();
    await Assert.That(_liveness().IsMatch(sql["wh_instance_liveness"])).IsTrue();
    await Assert.That(_liveness().IsMatch("SELECT 1 FROM t WHERE last_heartbeat_at > now() - interval '1 minute'")).IsTrue();
    await Assert.That(sql.ContainsKey("_advisory_lock_held_elsewhere")).IsFalse()
      .Because("a function dropped after its last definition (#2044) is not run by any database, so it is not scanned");
    await Assert.That(sql.ContainsKey("wh_role_assignment_status")).IsTrue()
      .Because("a drop followed by a new definition (drop_all_overloads, then CREATE) leaves the new definition");
    await Assert.That(_liveness().IsMatch(sql["claim_work"])).IsFalse()
      .Because("claim_work ranks its peers through the rule (317); a comment that mentions pg_stat_activity is not code");
  }

  private static async Task _assertOnlyAllowedAsync(Regex pattern, Dictionary<string, string> allowlist, string what) {
    var offenders = _csharpSources()
      .Where(f => !allowlist.ContainsKey(f.Key) && pattern.IsMatch(f.Value))
      .Select(f => f.Key)
      .ToList();
    await Assert.That(offenders).IsEmpty().Because($"{what}: {string.Join(", ", offenders)}");
  }

  private static async Task _assertSqlOnlyAllowedAsync(Regex pattern, Dictionary<string, string> allowlist, string what) {
    var offenders = _latestSqlFunctions()
      .Where(f => !allowlist.ContainsKey(f.Key) && pattern.IsMatch(f.Value))
      .Select(f => f.Key)
      .ToList();
    await Assert.That(offenders).IsEmpty().Because($"a SQL function {what}: {string.Join(", ", offenders)}");
  }

  /// <summary>Every C# source file under src, by repository-relative path, with comments removed.</summary>
  private static Dictionary<string, string> _csharpSources() =>
    Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               // Generated output a build caches beside the sources (.whizbang/cache) is not source.
               && !f.Contains($"{Path.DirectorySeparatorChar}.whizbang{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      .ToDictionary(
        f => Path.GetRelativePath(_repoRoot, f).Replace('\\', '/'),
        f => _csComment().Replace(File.ReadAllText(f), string.Empty),
        StringComparer.Ordinal);

  /// <summary>
  /// Each SQL function's last definition in the migration corpus, by name, with comments removed. A function whose
  /// last word is a drop is not in the result: a database does not run it.
  /// </summary>
  private static Dictionary<string, string> _latestSqlFunctions() {
    var latest = new Dictionary<string, string>(StringComparer.Ordinal);
    var migrations = Path.Combine(_repoRoot, "src", "Whizbang.Data.Postgres", "Migrations");
    foreach (var file in Directory.EnumerateFiles(migrations, "*.sql").Order(StringComparer.Ordinal)) {
      var sql = _sqlComment().Replace(File.ReadAllText(file), string.Empty);
      var definitions = _sqlFunction().Matches(sql).Select(m => (m.Index, Name: m.Groups[1].Value, Body: (string?)m.Groups[2].Value));
      var drops = _sqlFunctionDrop().Matches(sql)
        .Select(m => (m.Index, Name: m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, Body: (string?)null));
      foreach (var (_, name, body) in definitions.Concat(drops).OrderBy(e => e.Index)) {
        if (body is null) {
          _ = latest.Remove(name);
        } else {
          latest[name] = body;
        }
      }
    }
    return latest;
  }

  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core"))) {
        return dir;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate whizbang repo root from AppContext.BaseDirectory");
  }
}
