// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #2594 gravity: every role is declared once, in the <see cref="RoleCatalog"/>. Every call in <c>src</c> that votes for
/// a role names it by a constant the catalog declares, or forwards a name from a place listed here with the reason (each
/// one a name the elector checks against the catalog at run time). Every standing role the framework declares has
/// exactly one loop that holds it. A new acquire call with an undeclared role, or a second loop for a standing role,
/// fails this test.
/// </summary>
/// <docs>operations/startup/capabilities-and-duties#the-role-catalog</docs>
[Category("Startup")]
public partial class RoleDeclarationGuardTests {
  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>The constants a call may name, with their values, so the test asks the catalog about the value itself.</summary>
  private static readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal) {
    ["StartupDuties.MIGRATOR"] = StartupDuties.MIGRATOR,
    ["StartupDuties.MAINTAINER"] = StartupDuties.MAINTAINER,
    ["PartitionAssignerOptions.ROLE"] = PartitionAssignerOptions.ROLE,
    ["CommitOrderStamperOptions.ROLE"] = CommitOrderStamperOptions.ROLE,
  };

  /// <summary>Calls that forward a role name they were given, by file and argument, with where the name comes from.</summary>
  private static readonly Dictionary<string, string> _forwarders = new(StringComparer.Ordinal) {
    ["src/Whizbang.Core/Startup/IDutyElector.cs:duty"] = "the default multi-role vote, one call per duty it was given",
    ["src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs:duty"] = "the elector's own multi-role vote, for a duty it "
      + "could not vote for in the batch; the elector checks every duty against the catalog first",
    ["src/Whizbang.Core/Perspectives/PerspectiveRebuildGuard.cs:duty"] = "PerspectiveRebuildDuty.For(table): the "
      + "perspective-rebuild: family the catalog declares by prefix",
    ["src/Whizbang.Core/Startup/StartupPipelineRunner.cs:duty"] = "a startup step's RequiredCapability, which a host "
      + "declares with AddWhizbangRole; the elector refuses one it did not",
    ["src/Whizbang.Core/Startup/DutyHolderWorker.cs:roles"] = "the managed roles of RoleAssignmentOptions.Roles that have "
      + "work, which AddWhizbangRole fills",
  };

  /// <summary>The standing roles held by the generic holder loop rather than by a loop of their own.</summary>
  private static readonly Dictionary<string, string> _heldByTheHolderLoop = new(StringComparer.Ordinal) {
    [StartupDuties.MAINTAINER] = "DutyHolderWorker holds it for its owed work and its acquisition work",
  };

  [GeneratedRegex(@"\bTryAcquire(?:Many)?Async\(\s*(?!string\b|IReadOnlyList<)([^,()]+?)\s*,")]
  private static partial Regex _acquireCall();

  [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
  private static partial Regex _csComment();

  [Test]
  public async Task TheGuard_ReadsTheRealCallSitesAsync() {
    await Assert.That(_calls().Count).IsGreaterThanOrEqualTo(8).Because("a guard over no calls proves nothing");
  }

  [Test]
  public async Task EveryAcquireCall_NamesADeclaredRole_OrForwardsOneFromAListedPlaceAsync() {
    var catalog = new RoleCatalog([]);
    var wrong = new List<string>();
    foreach (var (file, argument) in _calls()) {
      if (_constants.TryGetValue(argument, out var role)) {
        if (!catalog.Declares(role)) {
          wrong.Add($"{file}: votes for '{role}' ({argument}), which the RoleCatalog does not declare");
        }
      } else if (!_forwarders.ContainsKey($"{file}:{argument}")) {
        wrong.Add($"{file}: votes for '{argument}', which is neither a constant the catalog declares nor a listed forwarder");
      }
    }

    await Assert.That(wrong).IsEmpty();
  }

  [Test]
  public async Task EveryFrameworkStandingRole_HasExactlyOneLoopThatHoldsItAsync() {
    var calls = _calls();
    var wrong = new List<string>();
    foreach (var declaration in RoleCatalog.FrameworkRoles.Where(d => d.Kind == RoleKind.Standing && !d.IsPrefix)) {
      var holders = calls.Count(c => _constants.TryGetValue(c.Argument, out var role) && role == declaration.Name)
        + (_heldByTheHolderLoop.ContainsKey(declaration.Name) ? 1 : 0);
      if (holders != 1) {
        wrong.Add($"'{declaration.Name}' is held by {holders} loops");
      }
    }

    await Assert.That(wrong).IsEmpty();
    await Assert.That(RoleCatalog.FrameworkRoles.Count(d => d.Kind == RoleKind.Standing)).IsGreaterThanOrEqualTo(3);
  }

  [Test]
  public async Task EveryListedForwarder_StillForwardsAsync() {
    var found = _calls().Select(c => $"{c.File}:{c.Argument}").ToHashSet(StringComparer.Ordinal);

    await Assert.That(_forwarders.Keys.Where(k => !found.Contains(k)).ToList()).IsEmpty()
      .Because("an entry for a call that is gone would let the next one in unexamined");
  }

  [Test]
  public async Task TheScan_FindsACall_AndSkipsTheDeclarationsAsync() {
    await Assert.That(_acquireCall().Match("await elector.TryAcquireAsync(\"nightly\", ct)").Groups[1].Value).IsEqualTo("\"nightly\"");
    await Assert.That(_acquireCall().Match("await e.TryAcquireManyAsync(roles, ct)").Groups[1].Value).IsEqualTo("roles");
    await Assert.That(_acquireCall().IsMatch("Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken ct);")).IsFalse();
    await Assert.That(_acquireCall().IsMatch("TryAcquireManyAsync(IReadOnlyList<string> duties, CancellationToken ct)")).IsFalse();
  }

  private static List<(string File, string Argument)> _calls() {
    var calls = new List<(string, string)>();
    foreach (var file in Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
               .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}.whizbang{Path.DirectorySeparatorChar}", StringComparison.Ordinal))) {
      var relative = Path.GetRelativePath(_repoRoot, file).Replace('\\', '/');
      var source = _csComment().Replace(File.ReadAllText(file), string.Empty);
      calls.AddRange(_acquireCall().Matches(source).Select(m => (relative, m.Groups[1].Value)));
    }
    return calls;
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
