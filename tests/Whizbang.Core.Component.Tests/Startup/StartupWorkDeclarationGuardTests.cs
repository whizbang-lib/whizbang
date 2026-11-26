// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #2596 gravity: the Converge stage's heavy work runs only through a <see cref="StartupWorkSession"/>, under a name
/// <see cref="FrameworkStartupWork"/> declares with its scope. In both drivers' converge code, every call to a heavy
/// helper (the analyze, the managed-object reconcile, the registry check, the storage settings) sits inside a session
/// run of a declared name, and each driver runs every framework unit it has. A heavy call added outside a session, or a
/// run of an undeclared name, fails this test.
/// </summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
[Category("Startup")]
public partial class StartupWorkDeclarationGuardTests {
  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>The converge code of each driver, with the framework units it runs.</summary>
  private static readonly Dictionary<string, string[]> _convergeFiles = new(StringComparer.Ordinal) {
    ["src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs"] = [
      nameof(FrameworkStartupWork.ANALYZE_NEW_INDEXES), nameof(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE),
      nameof(FrameworkStartupWork.PERSPECTIVE_REGISTRY), nameof(FrameworkStartupWork.PERSPECTIVE_STORAGE_SETTINGS),
    ],
    // The Dapper driver's pass queues no tables for analysis, so it has no analyze step to run.
    ["src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs"] = [
      nameof(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE), nameof(FrameworkStartupWork.PERSPECTIVE_REGISTRY),
      nameof(FrameworkStartupWork.PERSPECTIVE_STORAGE_SETTINGS),
    ],
  };

  /// <summary>The constants the declared names are reached by, with their values.</summary>
  private static readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal) {
    [nameof(FrameworkStartupWork.ANALYZE_NEW_INDEXES)] = FrameworkStartupWork.ANALYZE_NEW_INDEXES,
    [nameof(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE)] = FrameworkStartupWork.MANAGED_OBJECT_RECONCILE,
    [nameof(FrameworkStartupWork.PERSPECTIVE_REGISTRY)] = FrameworkStartupWork.PERSPECTIVE_REGISTRY,
    [nameof(FrameworkStartupWork.PERSPECTIVE_STORAGE_SETTINGS)] = FrameworkStartupWork.PERSPECTIVE_STORAGE_SETTINGS,
  };

  [GeneratedRegex(@"\.RunAsync\(\s*(?:global::Whizbang\.Core\.Startup\.)?FrameworkStartupWork\.([A-Z_]+)")]
  private static partial Regex _sessionRun();

  [GeneratedRegex(@"\b(?:IndexStatistics\.AnalyzeQueuedAsync|ManagedSchemaReconciler\.RunAsync|ManagedSchemaReconcile\.RunAsync|PerspectiveRegistryCurrency\.(?:ConvergeAsync|ReconcileIfStaleAsync)|PerspectiveStorageParameters\.ReconcileAsync|PerspectiveStorageReconcile\.RunAsync)\(")]
  private static partial Regex _heavyCall();

  [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
  private static partial Regex _csComment();

  [Test]
  public async Task EveryHeavyConvergeCall_RunsInsideASessionRunOfADeclaredNameAsync() {
    var wrong = new List<string>();
    var heavyCalls = 0;
    foreach (var file in _convergeFiles.Keys) {
      var source = _source(file);
      var spans = _runs(source).ToList();
      foreach (Match call in _heavyCall().Matches(source)) {
        heavyCalls++;
        if (!spans.Any(s => call.Index > s.Start && call.Index < s.End)) {
          wrong.Add($"{file}: {call.Value} runs outside a StartupWorkSession run, so neither the cap nor the once-per-build rule applies");
        }
      }
    }

    await Assert.That(wrong).IsEmpty();
    await Assert.That(heavyCalls).IsGreaterThanOrEqualTo(8).Because("a guard over no calls proves nothing");
  }

  [Test]
  public async Task EverySessionRun_NamesADeclaredUnit_AndEachDriverRunsEveryUnitItHasAsync() {
    var wrong = new List<string>();
    foreach (var (file, expected) in _convergeFiles) {
      var named = _runs(_source(file)).Select(r => r.Constant).ToList();
      foreach (var constant in named) {
        if (!_constants.TryGetValue(constant, out var value) || FrameworkStartupWork.Find(value) is null) {
          wrong.Add($"{file}: runs FrameworkStartupWork.{constant}, which nothing declares");
        }
      }
      if (!named.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))) {
        wrong.Add($"{file}: runs [{string.Join(", ", named)}], expected each of [{string.Join(", ", expected)}] once");
      }
    }

    await Assert.That(wrong).IsEmpty();
    await Assert.That(_constants.Values.Order(StringComparer.Ordinal))
      .IsEquivalentTo(FrameworkStartupWork.Declarations.Select(d => d.Name).Order(StringComparer.Ordinal))
      .Because("every declared unit has its constant here, so a new one is checked as well");
  }

  [Test]
  public async Task TheScan_FindsARunsSpan_AndAHeavyCallWithinItAsync() {
    const string sample = "x; await work.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, i, async ct => "
      + "(await PerspectiveRegistryCurrency.ConvergeAsync(f, s)) != F, ct); IndexStatistics.AnalyzeQueuedAsync(c);";
    var runs = _runs(sample).ToList();

    await Assert.That(runs.Count).IsEqualTo(1);
    await Assert.That(runs[0].Constant).IsEqualTo(nameof(FrameworkStartupWork.PERSPECTIVE_REGISTRY));
    var calls = _heavyCall().Matches(sample).Select(m => m.Index).ToList();
    await Assert.That(calls.Count).IsEqualTo(2);
    await Assert.That(calls[0] > runs[0].Start && calls[0] < runs[0].End).IsTrue();
    await Assert.That(calls[1] > runs[0].End).IsTrue().Because("the call after the run's closing parenthesis is outside it");
  }

  /// <summary>Each session run: the constant it names and the span of its argument list, parentheses balanced.</summary>
  private static IEnumerable<(string Constant, int Start, int End)> _runs(string source) {
    foreach (Match run in _sessionRun().Matches(source)) {
      var open = run.Index + ".RunAsync".Length;
      var depth = 0;
      var end = open;
      for (var i = open; i < source.Length; i++) {
        if (source[i] == '(') {
          depth++;
        } else if (source[i] == ')' && --depth == 0) {
          end = i;
          break;
        }
      }
      yield return (run.Groups[1].Value, open, end);
    }
  }

  private static string _source(string file) =>
    _csComment().Replace(File.ReadAllText(Path.Combine(_repoRoot, file)), string.Empty);

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
