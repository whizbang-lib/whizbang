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
/// Entries marked <c>PENDING</c> are what slice 1 of #2022 has not folded in yet (the advisory-lock duties and the raw
/// channels); each names the part of #2027 that removes it.
/// </para>
/// </remarks>
/// <docs>contributors/gravity</docs>
[Category("Startup")]
public partial class CoordinationSourceGuardTests {
  private const string PENDING_DUTIES = "PENDING #2027 part 2: an advisory-lock duty, to be held as a role assignment with a lease and an epoch";
  private const string PENDING_CHANNELS = "PENDING #2027 part 4: a raw notification channel, to move onto the signal bus";
  private const string EXCLUSION = "a transaction-scoped advisory lock: mutual exclusion for one statement or DDL pass, released at commit; not a duty";

  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>C# files that may name a notification channel.</summary>
  private static readonly Dictionary<string, string> _notifyAllowlist = new(StringComparer.Ordinal) {
    ["src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs"] = "the one LISTEN connection per instance: the signal bus's transport underneath",
    ["src/Whizbang.Data.Postgres/Notifications/PostgresSignalTransport.cs"] = "the signal bus's Postgres transport",
    ["src/Whizbang.Data.Postgres/Notifications/PgAppSignalChannel.cs"] = PENDING_CHANNELS,
  };

  /// <summary>C# files that may take or release an advisory lock.</summary>
  private static readonly Dictionary<string, string> _advisoryAllowlist = new(StringComparer.Ordinal) {
    ["src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs"] = PENDING_DUTIES,
    ["src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs"] = PENDING_DUTIES + " (the legacy-lock bridge)",
    ["src/Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs"] = PENDING_DUTIES + " (the stamper's own lock)",
    ["src/Whizbang.Data.Postgres/AdvisoryLockRelease.cs"] = PENDING_DUTIES + " (the release of a session duty lock)",
    ["src/Whizbang.Data.Postgres/Schema/ManagedSchemaReconciler.cs"] = PENDING_DUTIES + " (a session lock per drop) and " + EXCLUSION,
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
    ["src/Whizbang.Data.Postgres/AdvisoryLockProbe.cs"] = PENDING_DUTIES + " (tells a live holder of a duty lock from a dead one)",
  };

  /// <summary>SQL functions (by their last definition) that may judge liveness or read session activity.</summary>
  private static readonly Dictionary<string, string> _sqlLivenessAllowlist = new(StringComparer.Ordinal) {
    ["wh_instance_liveness"] = "THE liveness rule",
    ["wh_direct_alive_lock_holders"] = "the lock half of the rule",
    ["register_instance_heartbeat"] = "a write throttle on the beat itself; it judges no peer",
    ["_advisory_lock_held_elsewhere"] = PENDING_DUTIES + " (sees a legacy duty lock)",
    ["wh_end_lapsed_bridge"] = PENDING_DUTIES + " (ends a lapsed legacy-lock bridge)",
    ["wh_vote_role"] = PENDING_DUTIES + " (the vote refuses while a legacy lock is held)",
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
  public async Task TheScans_SeeTheMechanismsTheyGuardAsync() {
    // A guard that reads nothing passes everything: prove each scan finds what it exists to find.
    var csharp = _csharpSources();
    var sql = _latestSqlFunctions();

    await Assert.That(_notify().IsMatch(csharp["src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs"])).IsTrue();
    await Assert.That(_advisory().IsMatch(csharp["src/Whizbang.Data.Postgres/SchemaInitializationLock.cs"])).IsTrue();
    await Assert.That(_liveness().IsMatch(sql["wh_instance_liveness"])).IsTrue();
    await Assert.That(_liveness().IsMatch("SELECT 1 FROM t WHERE last_heartbeat_at > now() - interval '1 minute'")).IsTrue();
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

  /// <summary>Each SQL function's last definition in the migration corpus, by name, with comments removed.</summary>
  private static Dictionary<string, string> _latestSqlFunctions() {
    var latest = new Dictionary<string, string>(StringComparer.Ordinal);
    var migrations = Path.Combine(_repoRoot, "src", "Whizbang.Data.Postgres", "Migrations");
    foreach (var file in Directory.EnumerateFiles(migrations, "*.sql").Order(StringComparer.Ordinal)) {
      foreach (Match match in _sqlFunction().Matches(File.ReadAllText(file))) {
        latest[match.Groups[1].Value] = _sqlComment().Replace(match.Groups[2].Value, string.Empty);
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
