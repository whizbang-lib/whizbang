// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Source-level guard for the one ownership rule of the integrity engine (#2186). Whether an origin's count of a type
/// stands is decided in exactly one place, <c>IntegrityDomainOwnership.IsNotAuthoritative</c>: the checkpoint gap
/// tracker, the manifest comparison and the ledger sweep each call it, so they can never disagree about which origin
/// may be compared against, asked for repair, or kept on the ledger. This reads the source tree, so a comparison path
/// that judges ownership another way (the older map-only check, or the map's owner compared by hand) fails here:
/// <list type="bullet">
/// <item><description>every comparison path calls the shared rule and reads no owner itself;</description></item>
/// <item><description>the older map-only check is gone, so no path can fall back to it;</description></item>
/// <item><description>every file that builds the ownership map is listed here as a comparison path, or with the
/// attribution question it answers instead, so a new comparison path cannot slip in unclassified.</description></item>
/// </list>
/// Each scan is checked against a fact it must find, so a scan that looks in the wrong place fails instead of passing on
/// nothing.
/// </summary>
/// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
[Category("Core")]
[Category("Messaging")]
public partial class IntegrityOwnershipRuleSourceGuardTests {
  private static readonly string _repoRoot = _findRepoRoot();

  private const string RULE = "src/Whizbang.Core/Messaging/IntegrityDomainOwnership.cs";

  /// <summary>The integrity comparison paths: each judges an origin's count by the shared rule.</summary>
  private static readonly string[] _comparisonPaths = [
    "src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs",
    "src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs",
    "src/Whizbang.Core/Workers/IntegrityAuditWorker.cs",
  ];

  /// <summary>
  /// The files that build the ownership map to answer an attribution question (who wrote an event, or whom to ask
  /// for it), never whether an origin's count stands.
  /// </summary>
  private static readonly Dictionary<string, string> _attributionSites = new(StringComparer.Ordinal) {
    ["src/Whizbang.Core/Workers/OriginReattributionWorker.cs"] = "files pre-origin history under the owner that wrote it",
    ["src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs"] = "directs a backfill at the owner of a newly consumed type",
    ["src/Whizbang.Core/Workers/InboxDispatchWorker.cs"] = "stores a redelivered child under its type's owner (MisattributedOrigin)",
    ["src/Whizbang.Data.Postgres/MisattributedOriginCorrectionStep.cs"] = "moves stored events to their type's owner",
  };

  /// <summary>The files that read a type's owner from the map directly, each for an attribution question.</summary>
  private static readonly string[] _ownerReaders = [
    "src/Whizbang.Core/Messaging/MisattributedOrigin.cs",
    "src/Whizbang.Core/Workers/OriginReattributionWorker.cs",
    "src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs",
  ];

  [GeneratedRegex(@"\.IsNotAuthoritative\(")]
  private static partial Regex _sharedRule();

  [GeneratedRegex(@"\bIsOwnedByAnother\b")]
  private static partial Regex _mapOnlyCheck();

  // The map's owner read directly; OwnedNamespaceMatcher.FindOwner is the routing matcher, not the map.
  [GeneratedRegex(@"(?<!OwnedNamespaceMatcher)\.FindOwner\(")]
  private static partial Regex _ownerRead();

  [GeneratedRegex(@"new\s+(?:[\w.]+\.)?IntegrityDomainOwnership\s*\(")]
  private static partial Regex _mapBuilt();

  [GeneratedRegex(@"OwnedNamespaceMatcher\.")]
  private static partial Regex _domainMatcher();

  /// <summary>Every comparison path calls the shared rule, and judges ownership no other way.</summary>
  [Test]
  public async Task ComparisonPaths_JudgeOwnershipOnlyThroughTheSharedRuleAsync() {
    var callers = await _filesMatchingAsync(_sharedRule());

    await Assert.That(callers).IsEquivalentTo(_comparisonPaths)
      .Because("a new caller of IsNotAuthoritative is a comparison path; list it here so the checks below cover it");
    foreach (var path in _comparisonPaths) {
      var code = await _codeLinesAsync(path);
      await Assert.That(code.Any(l => _ownerRead().IsMatch(l))).IsFalse()
        .Because($"{path} compares against origins; reading the map's owner itself is a second ownership rule");
      await Assert.That(code.Any(l => _domainMatcher().IsMatch(l))).IsFalse()
        .Because($"{path} compares against origins; matching owned domains itself is a second ownership rule");
    }
  }

  /// <summary>The older map-only check is gone, so no path can judge by it.</summary>
  [Test]
  public async Task TheMapOnlyCheck_IsUsedNowhereAsync() {
    var sites = await _filesMatchingAsync(_mapOnlyCheck());

    await Assert.That(sites).IsEmpty()
      .Because("IsOwnedByAnother judged by the map alone and missed the types this service owns; IsNotAuthoritative is the rule");
  }

  /// <summary>Every file that builds the ownership map is a comparison path or a listed attribution site.</summary>
  [Test]
  public async Task EverySiteThatBuildsTheMap_IsClassifiedAsync() {
    var sites = await _filesMatchingAsync(_mapBuilt());

    await Assert.That(sites).Contains(_comparisonPaths[0]).Because("the scan must find the checkpoint receptor's map");
    await Assert.That(sites).IsEquivalentTo(_comparisonPaths.Concat(_attributionSites.Keys))
      .Because("a new place that builds the ownership map either compares against an origin (use IsNotAuthoritative and "
             + "list it among the comparison paths) or answers an attribution question (list it with that question)");
  }

  /// <summary>Only attribution sites read a type's owner from the map directly.</summary>
  [Test]
  public async Task OnlyAttributionSites_ReadTheOwnerDirectlyAsync() {
    var sites = (await _filesMatchingAsync(_ownerRead())).Where(s => s != RULE).ToList();

    await Assert.That(sites).Contains(_ownerReaders[0]).Because("the scan must find MisattributedOrigin's owner read");
    await Assert.That(sites).IsEquivalentTo(_ownerReaders)
      .Because("reading the owner to decide whether an origin's count stands is the shared rule's job");
  }

  private static async Task<List<string>> _codeLinesAsync(string relative) =>
    [.. (await File.ReadAllLinesAsync(Path.Combine(_repoRoot, relative)))
      .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))];

  private static async Task<List<string>> _filesMatchingAsync(Regex pattern) {
    var hits = new List<string>();
    foreach (var path in Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(_repoRoot, path).Replace('\\', '/');
      if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
          || relative.Contains("/.whizbang/", StringComparison.Ordinal) || relative.Contains("/Templates/", StringComparison.Ordinal)) {
        continue;
      }
      var lines = await File.ReadAllLinesAsync(path);
      if (lines.Any(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && pattern.IsMatch(l))) {
        hits.Add(relative);
      }
    }
    hits.Sort(StringComparer.Ordinal);
    return hits;
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
