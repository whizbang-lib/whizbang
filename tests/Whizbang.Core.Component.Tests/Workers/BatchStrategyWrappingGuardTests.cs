// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Component.Tests.Workers;

/// <summary>
/// Every outbox and inbox batch strategy the library registers resolves wrapped in the batch buffer's bound (#2220).
/// The resolution tests cover the registrations that exist; this guard reads the source so a registration added later
/// that hands out a strategy unwrapped fails here instead of shipping an unbounded buffer.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
public partial class BatchStrategyWrappingGuardTests {
  [GeneratedRegex(@"\b(?:Try)?Add(?:Singleton|Scoped|Transient)\s*<\s*I(?:Outbox|Inbox)BatchStrategy\s*>\s*\((?<factory>[^;]*);",
    RegexOptions.Singleline)]
  private static partial Regex _strategyRegistration();

  [Test]
  public async Task EveryBatchStrategyRegistration_HandsOutTheBoundedDecoratorAsync() {
    var root = _findRepoRoot();
    var registrations = new List<(string Path, string Factory)>();
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)) {
      if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
        continue;
      }
      var code = await File.ReadAllTextAsync(file);
      registrations.AddRange(_strategyRegistration().Matches(code)
        .Select(m => (Path.GetRelativePath(root, file), m.Groups["factory"].Value)));
    }

    await Assert.That(registrations.Count).IsGreaterThanOrEqualTo(4)
      .Because("the default outbox and inbox registrations and both strategy overrides must be found, or the scan reads nothing");
    var unwrapped = registrations.Where(r => !r.Factory.Contains("Bounded", StringComparison.Ordinal)).ToList();
    await Assert.That(unwrapped).IsEmpty()
      .Because("a batch strategy must be registered wrapped in BoundedOutboxBatchStrategy or BoundedInboxBatchStrategy: "
        + string.Join("; ", unwrapped.Select(u => u.Path + ": " + u.Factory.Trim())));
  }

  [GeneratedRegex(@"\bGet(?:Required)?Service<\s*I(?:Outbox|Inbox)BatchStrategy\s*>")]
  private static partial Regex _strategyResolution();

  [Test]
  public async Task OnlyTheResolutionHelper_ResolvesABatchStrategyToAppendToAsync() {
    var root = _findRepoRoot();
    var resolving = new List<string>();
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)) {
      if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
        continue;
      }
      if (_strategyResolution().IsMatch(await File.ReadAllTextAsync(file))) {
        resolving.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
      }
    }

    await Assert.That(resolving).Contains("src/Whizbang.Core/Workers/BatchStrategyResolution.cs")
      .Because("the scan must find the helper itself, or it is not reading the source tree");
    await Assert.That(resolving.Where(p => p != "src/Whizbang.Core/Workers/BatchStrategyResolution.cs")).IsEmpty()
      .Because("resolve the strategy through BatchStrategyResolution, which bounds a directly registered one: "
        + string.Join(", ", resolving));
  }

  private static string _findRepoRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whizbang.slnx"))) {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Whizbang.slnx not found above the test binary.");
  }
}
