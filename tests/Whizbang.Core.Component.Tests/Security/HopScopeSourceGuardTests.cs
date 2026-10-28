// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Source-level guard for the scope a message carries on its hop (#1854). Every dispatch path builds it through
/// one helper (<c>HopScope</c>), and the cross-path parity table (<c>HopScopeDispatchParityTests</c>) covers the
/// paths. A site that builds a hop scope its own way is how scope extensions and claims were dropped at the
/// first hop, so this reads the source tree and fails on a new one:
/// <list type="bullet">
/// <item><description>only <c>HopScope</c> serializes a scope for a hop, with its extensions and propagated claims;</description></item>
/// <item><description>a hop scope rebuilt from a tenant and user id alone is allowed only where a stored row is read
/// back and nothing more was kept, and each such site is listed here with why.</description></item>
/// </list>
/// Each scan is checked against a fact it must find, so a scan that looks in the wrong place fails instead of
/// passing on nothing.
/// </summary>
/// <docs>fundamentals/security/message-security#hop-scope</docs>
[Category("Core")]
[Category("Security")]
public partial class HopScopeSourceGuardTests {
  private static readonly string _repoRoot = _findRepoRoot();

  private const string HELPER = "src/Whizbang.Core/Security/HopScope.cs";

  /// <summary>Sites that rebuild a hop scope from a stored tenant and user id, and why that is all they have.</summary>
  private static readonly Dictionary<string, string> _idOnlyReadBacks = new(StringComparer.Ordinal) {
    ["src/Whizbang.Data.Dapper.Postgres/EventEnvelopeJsonbAdapter.cs"] =
      "restores a stored event's scope column onto a synthesized hop when the row kept no envelope hops",
    ["src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs"] =
      "re-reads a stored row's tenant and user to re-run lifecycle stages during reconciliation",
  };

  [GeneratedRegex(@"ScopeDelta\.ForHop\(")]
  private static partial Regex _hopSerialization();

  [GeneratedRegex(@"ScopeDelta\.FromSecurityContext\(")]
  private static partial Regex _idOnlyHopScope();

  /// <summary>Only the helper serializes a hop scope; a second builder is how the extensions were dropped.</summary>
  [Test]
  public async Task HopScope_IsSerializedOnlyByTheHelperAsync() {
    var sites = await _filesMatchingAsync(_hopSerialization());

    await Assert.That(sites).Contains(HELPER).Because("the scan must find the helper itself, or it is not reading the source tree");
    await Assert.That(sites).IsEquivalentTo([HELPER])
      .Because("build a hop scope with HopScope.FromAmbient, FromMessageContext or FromEnvelope");
  }

  /// <summary>A hop scope of the tenant and user alone drops the extensions; only stored-row read-backs may build one.</summary>
  [Test]
  public async Task IdOnlyHopScopes_AreOnlyTheListedReadBacksAsync() {
    var sites = await _filesMatchingAsync(_idOnlyHopScope());

    await Assert.That(sites).Contains("src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs")
      .Because("the scan must find a known read-back, or it is not reading the source tree");
    await Assert.That(sites).IsEquivalentTo(_idOnlyReadBacks.Keys)
      .Because("a hop scope built from a tenant and user id drops the scope's extensions and claims; dispatch it "
             + "through HopScope, or list a read-back here with why the ids are all it has");
  }

  private static async Task<List<string>> _filesMatchingAsync(Regex pattern) {
    var hits = new List<string>();
    foreach (var path in Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(_repoRoot, path).Replace('\\', '/');
      if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
          || relative.Contains("/.whizbang/", StringComparison.Ordinal)) {
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
