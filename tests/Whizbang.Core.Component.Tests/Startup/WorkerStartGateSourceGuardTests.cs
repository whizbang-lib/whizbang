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
/// for everything else. The same scan holds the registration end: no sample hosts a worker with plain
/// <c>AddHostedService</c>, and no worker the framework hosts with its start point is hosted a second time that way.
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

  [GeneratedRegex(@"\bAddHostedService\s*(?:<\s*([\w.]+)\s*>|\(\s*\w+\s*=>\s*\w+\s*\.\s*GetRequiredService<([\w.]+)>)")]
  private static partial Regex _plainHostedRegistration();

  [GeneratedRegex(@"\bAddHostedService\s*[<(]")]
  private static partial Regex _anyPlainHostedRegistration();

  [GeneratedRegex(@"\b(?:AddWhizbangWorker(?:AtSchemaReady)?|StartupStageGatedService|SchemaReadyGatedService)\s*<\s*([\w.]+)\s*>")]
  private static partial Regex _stagedByType();

  [GeneratedRegex(@"\b(?:AddWhizbangWorker(?:AtSchemaReady)?|AddHostedServiceAtStage)\s*\([^;]*?GetRequiredService<([\w.]+)>",
    RegexOptions.Singleline)]
  private static partial Regex _stagedByFactory();

  /// <summary>
  /// A sample is the path a consumer copies, so a sample hosts every worker, its own included, with the point it starts
  /// at. <c>AddHostedService</c> starts a worker with the host, before the schema is migrated, and for a worker the
  /// framework already hosts it adds a second, unstaged copy that does the same.
  /// </summary>
  [Test]
  public async Task NoSample_HostsAWorkerWithPlainAddHostedService_ButWithItsStartPointAsync() {
    var samples = await _sourceFilesAsync("samples");
    var plain = samples.Where(f => _anyPlainHostedRegistration().IsMatch(f.Code)).Select(f => f.Path).ToList();

    await Assert.That(samples.Where(f => f.Code.Contains("AddWhizbangWorker<Worker>()", StringComparison.Ordinal))
        .Select(f => f.Path))
      .Contains("samples/ECommerce/ECommerce.InventoryWorker/Program.cs")
      .Because("the scan must find a sample hosting its own worker with its start point, or it is not reading the samples");
    await Assert.That(plain).IsEmpty()
      .Because("host the sample's own worker with AddWhizbangWorker<TWorker>(), and drop a framework worker the "
        + "composition already hosts (AddWhizbang hosts the perspective worker, AddTransportConsumer the consumer) or "
        + "host it with AddWhizbangWorker(stage, factory); found in: " + string.Join(", ", plain));
  }

  /// <summary>
  /// A worker the framework hosts with its start point is never also hosted with plain <c>AddHostedService</c>, in the
  /// library or a sample: <c>AddHostedService</c> activates a second instance that starts with the host, before the
  /// schema is migrated, beside the staged one.
  /// </summary>
  [Test]
  public async Task NoStagedFrameworkWorker_IsAlsoHostedWithPlainAddHostedServiceAsync() {
    var library = await _sourceFilesAsync("src");
    var staged = _stagedWorkers(library);
    var plain = new List<(string Path, string Type)>();
    foreach (var (path, code) in library.Concat(await _sourceFilesAsync("samples"))) {
      foreach (Match m in _plainHostedRegistration().Matches(code)) {
        var type = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        plain.Add((path, _simpleName(type)));
      }
    }

    await Assert.That(staged).Contains("ClaimWorker").And.Contains("PerspectiveWorker").And.Contains("TransportConsumerWorker")
      .Because("the scan must find the workers the framework hosts with their start point");
    await Assert.That(plain.Select(p => p.Type)).Contains("LifecyclePhaseWorker")
      .Because("the scan must find a plain registration the library makes on purpose (startup machinery)");
    var doubled = plain.Where(p => staged.Contains(p.Type)).Select(p => $"{p.Type} in {p.Path}").ToList();
    await Assert.That(doubled).IsEmpty()
      .Because("the framework already hosts these with their start point; drop the plain registration: "
        + string.Join(", ", doubled));
  }

  [GeneratedRegex(@"\bGetServices<(?:Microsoft\.Extensions\.Hosting\.)?IHostedService>\(\)")]
  private static partial Regex _hostedServicesEnumeration();

  [GeneratedRegex(@"\bGetServices<(?:Microsoft\.Extensions\.Hosting\.)?IHostedService>\(\)\s*\.\s*OfType<([\w.]+)>")]
  private static partial Regex _hostedServicesByType();

  /// <summary>
  /// A worker hosted with its start point is inside its stage wrapper, so a lookup by type over the hosted services
  /// finds nothing and a fixture that waits on the worker's signals waits forever. The one way to find a hosted worker
  /// is <c>Whizbang.Testing.Workers.HostedWorkers</c> (<c>GetHostedWorker&lt;T&gt;()</c>, <c>GetHostedWorkers()</c>):
  /// a sample never enumerates the hosted services itself, and no test or sample looks a staged worker up by type among
  /// the wrappers. A registration test that filters to <c>GatedHostedService</c> on purpose, to assert that a worker is
  /// staged, reads the wrapper and is not a lookup.
  /// </summary>
  [Test]
  public async Task AHostedWorker_IsFoundThroughHostedWorkers_NotByTypeAmongTheWrappersAsync() {
    var staged = _stagedWorkers(await _sourceFilesAsync("src"));
    var samples = await _sourceFilesAsync("samples");
    var tests = await _sourceFilesAsync("tests");

    await Assert.That(tests.Where(f => f.Code.Contains("GetHostedWorker", StringComparison.Ordinal)).Select(f => f.Path))
      .Contains("tests/Whizbang.Testing.Tests/Workers/HostedWorkersTests.cs")
      .Because("the scan must find the lookup's own tests, or it is not reading the tests");

    var found = new List<string>();
    found.AddRange(samples.Where(f => _hostedServicesEnumeration().IsMatch(f.Code))
      .Select(f => $"{f.Path}: enumerates the hosted services (use GetHostedWorker<T>() or GetHostedWorkers())"));
    foreach (var (path, code) in samples.Concat(tests)) {
      foreach (Match m in _hostedServicesByType().Matches(code)) {
        if (staged.Contains(_simpleName(m.Groups[1].Value))) {
          found.Add($"{path}: looks {m.Groups[1].Value} up among the wrappers, which finds nothing (use GetHostedWorker<T>())");
        }
      }
    }
    await Assert.That(found).IsEmpty().Because(string.Join("; ", found));
  }

  /// <summary>The workers the library hosts with a start point, by simple type name.</summary>
  private static HashSet<string> _stagedWorkers(List<(string Path, string Code)> library) {
    var staged = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (_, code) in library) {
      foreach (Match m in _stagedByType().Matches(code)) {
        staged.Add(_simpleName(m.Groups[1].Value));
      }
      foreach (Match m in _stagedByFactory().Matches(code)) {
        staged.Add(_simpleName(m.Groups[1].Value));
      }
    }
    return staged;
  }

  private static string _simpleName(string type) => type[(type.LastIndexOf('.') + 1)..];

  /// <summary>Every source file under <paramref name="root"/>, with its code, comment lines removed.</summary>
  private static async Task<List<(string Path, string Code)>> _sourceFilesAsync(string root) {
    var files = new List<(string, string)>();
    foreach (var path in Directory.EnumerateFiles(Path.Combine(_repoRoot, root), "*.cs", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(_repoRoot, path).Replace('\\', '/');
      if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
          || relative.Contains("/.whizbang/", StringComparison.Ordinal)) {
        continue;
      }
      var lines = await File.ReadAllLinesAsync(path);
      files.Add((relative, string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))));
    }
    files.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
    return files;
  }

  /// <summary>Every source file in <c>src</c> that declares a hosted service, with its code, comments removed.</summary>
  private static async Task<List<(string Path, string Code)>> _hostedServiceFilesAsync() =>
    [.. (await _sourceFilesAsync("src")).Where(f => _hostedServiceDeclaration().IsMatch(f.Code))];

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
