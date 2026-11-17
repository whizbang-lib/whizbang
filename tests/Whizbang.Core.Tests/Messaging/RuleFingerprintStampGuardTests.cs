// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2362: every integrity message the framework builds carries its sender's rules. A receiver halts the pair on a
/// message without them, so a send site that forgot the fingerprint would halt every pair it talks to. This guard reads
/// every source file and fails on a construction of the four carriers whose initializer does not set it.
/// </summary>
[Category("Messaging")]
public partial class RuleFingerprintStampGuardTests {
  [GeneratedRegex(@"new (RequestIntegrityManifest|IntegrityManifest|IntegrityCheckpoint|RequestRedeliveryCommand)\s*\{")]
  private static partial Regex Construction();

  [Test]
  public async Task EveryIntegrityMessageTheFrameworkBuilds_CarriesItsRulesAsync() {
    var root = _repositoryRoot();
    var offenders = new List<string>();
    var constructions = 0;
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)) {
      if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
        continue;
      }
      var text = await File.ReadAllTextAsync(file);
      foreach (Match match in Construction().Matches(text)) {
        constructions++;
        if (!_initializer(text, match.Index + match.Length).Contains("RuleFingerprint =", StringComparison.Ordinal)) {
          offenders.Add($"{Path.GetRelativePath(root, file)}: new {match.Groups[1].Value}");
        }
      }
    }

    await Assert.That(constructions).IsGreaterThanOrEqualTo(13).Because("positive control: the scan found the send sites");
    await Assert.That(offenders).IsEmpty()
      .Because("these integrity messages carry no rule fingerprint, so every receiver would halt the pair: "
        + string.Join("; ", offenders));
  }

  /// <summary>The initializer's text from just after its opening brace to its matching closing brace.</summary>
  private static string _initializer(string text, int start) {
    var depth = 1;
    for (var i = start; i < text.Length; i++) {
      depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
      if (depth == 0) {
        return text[start..i];
      }
    }
    return text[start..];
  }

  private static string _repositoryRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null
        && !(Directory.Exists(Path.Combine(dir.FullName, "src")) && Directory.Exists(Path.Combine(dir.FullName, "tests")))) {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found above the test binaries");
  }
}
