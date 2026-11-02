// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Source-level guard for when a hosted service starts (#1636): a worker is registered with the point it starts at
/// (<c>AddWhizbangWorker</c>, <c>AddWhizbangWorkerAtSchemaReady</c>) and the host starts it there, so no hosted service
/// decides for itself when to start by holding the schema-ready gate. A hosted service in <c>src</c> that names
/// <c>ISchemaReadyGate</c> fails here, unless it is listed below as the startup machinery that opens or follows the gate
/// for everything else.
/// </summary>
/// <remarks>
/// The scan is checked against facts it must find (a worker it must recognize as a hosted service, and every listed
/// exception), so a scan that reads the wrong place or matches nothing fails instead of passing on nothing.
/// </remarks>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
[Category("Startup")]
public partial class WorkerStartGateSourceGuardTests {
  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>The hosted services that may name the schema-ready gate, and why.</summary>
  private static readonly Dictionary<string, string> _startupMachinery = new(StringComparer.Ordinal) {
    ["src/Whizbang.Data.Postgres/WhizbangDatabaseInitializerService.cs"] = "opens the gate at the end of Coordinate",
    ["src/Whizbang.Core/RunControl/LifecyclePhaseWorker.cs"] =
      "moves the lifecycle into Accept commands when the gate opens: it is the transition the data plane waits for",
    ["src/Whizbang.Core/Workers/ReadModelsReadyDriver.cs"] =
      "releases the read-model gate after the gate, which moves the lifecycle into Running",
    ["src/Whizbang.Core/Startup/StartupPipelineHosting.cs"] =
      "the Migrate startup step completes when the gate opens; the pipeline worker in the same file does not wait on it",
    ["src/Whizbang.Core/Startup/StartupStageGatedService.cs"] = "the wrapper that starts a worker at its stage",
    ["src/Whizbang.Core/Startup/SchemaReadyGatedService.cs"] = "the wrapper that starts a worker when the schema is ready",
  };

  [GeneratedRegex(@"\b(?:class|record)\s+\w+[^{;]*?:[^{;]*?\b(?:BackgroundService|IHostedService|IHostedLifecycleService)\b",
    RegexOptions.Singleline)]
  private static partial Regex _hostedServiceDeclaration();

  [GeneratedRegex(@"\bISchemaReadyGate\b")]
  private static partial Regex _schemaReadyGate();

  [Test]
  public async Task NoHostedService_DecidesItsOwnStartOnTheSchemaReadyGate_ButTheStartupMachineryAsync() {
    var hosted = await _hostedServiceFilesAsync();
    var naming = new List<string>();
    foreach (var (path, code) in hosted) {
      if (_schemaReadyGate().IsMatch(code)) {
        naming.Add(path);
      }
    }

    await Assert.That(hosted.Select(h => h.Path)).Contains("src/Whizbang.Core/Workers/ClaimWorker.cs")
      .Because("the scan must recognize a worker as a hosted service, or it is not reading the source tree");
    foreach (var listed in _startupMachinery.Keys) {
      await Assert.That(naming).Contains(listed)
        .Because($"{listed} is listed as startup machinery that names the gate; drop it from the list if it no longer does");
    }
    var unlisted = naming.Where(p => !_startupMachinery.ContainsKey(p)).ToList();
    await Assert.That(unlisted).IsEmpty()
      .Because("register the worker with AddWhizbangWorker (Accept commands by default) or AddWhizbangWorkerAtSchemaReady "
        + "and delete its own wait on the schema-ready gate; found in: " + string.Join(", ", unlisted));
  }

  /// <summary>Every source file in <c>src</c> that declares a hosted service, with its code, comments removed.</summary>
  private static async Task<List<(string Path, string Code)>> _hostedServiceFilesAsync() {
    var files = new List<(string, string)>();
    foreach (var path in Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(_repoRoot, path).Replace('\\', '/');
      if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
          || relative.Contains("/.whizbang/", StringComparison.Ordinal)) {
        continue;
      }
      var lines = await File.ReadAllLinesAsync(path);
      var code = string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
      if (_hostedServiceDeclaration().IsMatch(code)) {
        files.Add((relative, code));
      }
    }
    files.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
    return files;
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
