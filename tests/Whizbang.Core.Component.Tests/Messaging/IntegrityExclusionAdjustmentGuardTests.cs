// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Source-level guard for the one place an origin's answer has this side's integrity exclusions removed (#2588). The
/// manifest comparison applies <c>IntegrityExclusionAdjustment.ApplyAsync</c> once, before any comparison reads the
/// answer, and hands the undecided buckets to the type-level compare; the checkpoint comparison adds the excluded
/// counts after every recount. A comparison path that compares an origin's answer without the adjustment would turn
/// every exclusion into a mismatch against its origin, which is the one thing an exclusion must never do, so it fails
/// here:
/// <list type="bullet">
/// <item><description>the manifest receptor applies the adjustment before its first comparison;</description></item>
/// <item><description>every method that places a bucket (<c>PlaceHeld</c>) is one the adjusted answer reaches;</description></item>
/// <item><description>every checkpoint recount is followed by the excluded counts;</description></item>
/// <item><description>no other source file compares an origin's answer.</description></item>
/// </list>
/// </summary>
/// <docs>resilience/integrity-exclusions#cross-service-agreement</docs>
[Category("Core")]
[Category("Messaging")]
public partial class IntegrityExclusionAdjustmentGuardTests {
  private const string MANIFEST = "src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs";
  private const string CHECKPOINT = "src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs";
  private const string APPLY = "IntegrityExclusionAdjustment.ApplyAsync(";
  private const string CHECKPOINT_COUNTS = "IntegrityExclusionAdjustment.CheckpointCountsAsync(";
  private const string CORE_METHOD = "private async Task _handleCoreAsync(IntegrityManifest message";

  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>The methods a placed bucket may be compared in: each receives the adjusted answer.</summary>
  private static readonly string[] _adjustedComparisons = ["_handleCoreAsync", "_healWhatTheAnswerStoredAsync", "_compareTypeLevelAsync"];

  [GeneratedRegex(@"\.CountReceivedFromOriginAsync\(")]
  private static partial Regex _recount();

  [GeneratedRegex(@"PlaceHeld\(|\.CountReceivedFromOriginAsync\(")]
  private static partial Regex _comparison();

  // A method's declaration: its name is the first word followed by '(' after the return type, which may hold tuples.
  [GeneratedRegex(@"(?:private|internal|public)\s+(?:static\s+)?async\s+Task[^\n]*?\s(?<name>\w+)\(")]
  private static partial Regex _method();

  [Test]
  public async Task TheManifestComparison_AppliesTheAdjustment_BeforeItComparesAnythingAsync() {
    var source = await File.ReadAllTextAsync(Path.Combine(_repoRoot, MANIFEST));

    var offenders = ManifestOffenders(source);

    await Assert.That(source).Contains(CORE_METHOD).Because("precondition: the scan reads the manifest comparison");
    await Assert.That(offenders).IsEmpty().Because(string.Join("; ", offenders));
  }

  [Test]
  public async Task EveryCheckpointRecount_IsFollowedByTheExcludedCountsAsync() {
    var source = await File.ReadAllTextAsync(Path.Combine(_repoRoot, CHECKPOINT));
    var recounts = _recount().Count(source);
    var adjusted = source.Split(CHECKPOINT_COUNTS).Length - 1;

    await Assert.That(recounts).IsGreaterThan(1).Because("precondition: the scan reads the checkpoint comparison's recounts");
    await Assert.That(adjusted).IsEqualTo(recounts)
      .Because("each recount of what this side received is short by the events it excluded, unless they are added back");
  }

  [Test]
  public async Task NoOtherSourceFile_ComparesAnOriginsAnswerAsync() {
    var allowed = new[] { MANIFEST, CHECKPOINT, "src/Whizbang.Core/Messaging/IntegrityDigestMath.cs" };
    var offenders = new List<string>();
    var scanned = 0;
    foreach (var file in Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)) {
      if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
        continue;
      }
      scanned++;
      var relative = Path.GetRelativePath(_repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
      if (!allowed.Contains(relative) && _comparison().IsMatch(await File.ReadAllTextAsync(file))) {
        offenders.Add(relative);
      }
    }

    await Assert.That(scanned).IsGreaterThan(500).Because("precondition: the scan read the source tree");
    await Assert.That(offenders).IsEmpty()
      .Because("a new comparison of an origin's answer must go through IntegrityExclusionAdjustment: " + string.Join(", ", offenders));
  }

  [Test]
  public async Task TheGuard_FlagsAComparisonMadeBeforeTheAdjustment_AndAPlacementOutsideTheAdjustedPathsAsync() {
    const string lateApply = """
      private async Task _handleCoreAsync(IntegrityManifest message, CancellationToken ct) {
        var placement = IntegrityDigestMath.PlaceHeld(origin, placed, null);
        var exclusions = await IntegrityExclusionAdjustment.ApplyAsync(services, message, ct);
      }
      """;
    const string strayPlacement = """
      private async Task _handleCoreAsync(IntegrityManifest message, CancellationToken ct) {
        var exclusions = await IntegrityExclusionAdjustment.ApplyAsync(services, message, ct);
      }
      private async Task _somethingNewAsync(IntegrityManifest message) {
        var placement = IntegrityDigestMath.PlaceHeld(origin, placed, null);
      }
      """;

    await Assert.That(ManifestOffenders(lateApply)).IsNotEmpty();
    await Assert.That(ManifestOffenders(strayPlacement)).IsNotEmpty();
  }

  /// <summary>Why a manifest comparison source breaks the rule; empty when it keeps it.</summary>
  internal static List<string> ManifestOffenders(string source) {
    var offenders = new List<string>();
    var core = source.IndexOf(CORE_METHOD, StringComparison.Ordinal);
    if (core < 0) {
      offenders.Add("the manifest comparison's core method was not found");
      return offenders;
    }
    var body = _bodyAt(source, core);
    var apply = body.IndexOf(APPLY, StringComparison.Ordinal);
    if (apply < 0) {
      offenders.Add("the core method does not apply the exclusion adjustment");
    }
    foreach (var call in new[] { "PlaceHeld(", "_handleTypeLevelAsync(", "_healWhatTheAnswerStoredAsync(", "ComputeStreamDigestsForChunkAsync(" }) {
      var at = body.IndexOf(call, StringComparison.Ordinal);
      if (at >= 0 && (apply < 0 || at < apply)) {
        offenders.Add($"{call} is reached before the adjustment");
      }
    }
    foreach (Match method in _method().Matches(source)) {
      var methodBody = _bodyAt(source, method.Index);
      if (methodBody.Contains("PlaceHeld(", StringComparison.Ordinal) && !_adjustedComparisons.Contains(method.Groups["name"].Value)) {
        offenders.Add($"{method.Groups["name"].Value} places a bucket the adjusted answer does not reach");
      }
    }
    return offenders;
  }

  private static string _bodyAt(string source, int from) {
    var open = source.IndexOf('{', from);
    var depth = 0;
    for (var i = open; i < source.Length; i++) {
      depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
      if (depth == 0) {
        return source[open..(i + 1)];
      }
    }
    return source[open..];
  }

  private static string _findRepoRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whizbang.slnx"))) {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
  }
}
