// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Whizbang.CLI.Audit;

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditCommand"/> end to end: a restored project on disk, OSV stubbed, and
/// the output and exit code a CI step sees.
/// </summary>
/// <tests>Whizbang.CLI/Audit/AuditCommand.cs</tests>
public class AuditCommandTests {
  private const string CORE = "SoftwareExtravaganza.Whizbang.Core";
  private const string CLEAN_LINE = "No published advisory affects";

  private sealed record Run(int ExitCode, string Output, string Error, StubOsvHandler Osv);

  private static async Task<Run> _runAsync(
      string[] args,
      StubOsvHandler? osv = null,
      TimeProvider? time = null,
      CancellationToken cancellationToken = default) {
    osv ??= new StubOsvHandler();
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = await AuditCommand.RunAsync(args, output, error, () => osv, time ?? TimeProvider.System, cancellationToken);
    return new Run(exitCode, output.ToString(), error.ToString(), osv);
  }

  /// <summary>OSV reporting one advisory of <paramref name="severity"/> against the first queried package.</summary>
  private static StubOsvHandler _osvReporting(string? severity) => new() {
    Respond = (r, _) => Task.FromResult(StubOsvHandler.Json(r.Method == HttpMethod.Post
      ? StubOsvHandler.EmptyBatchFor(r.Body!).Replace("[{}", "[{\"vulns\":[{\"id\":\"GHSA-cmd-0001\"}]}", StringComparison.Ordinal)
      : StubOsvHandler.Advisory("GHSA-cmd-0001", CORE, severity, "CVE-2026-1001", ["0.2616.0"]))),
  };

  private static string _sampleProject(AuditWorkspace workspace) =>
    workspace.AddProject("SampleApp", "SampleApp", AuditWorkspace.Fixture("SampleApp"));

  [Test]
  public async Task RunAsync_NoAdvisories_ExitsZeroAndSaysHowManyWereCheckedAsync() {
    using var workspace = new AuditWorkspace();

    var run = await _runAsync(["--project", _sampleProject(workspace)]);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_CLEAN);
    await Assert.That(run.Output).IsEqualTo(
      "No published advisory affects the Whizbang packages this project uses (5 packages checked)." + Environment.NewLine);
    await Assert.That(run.Error).IsEmpty();
  }

  [Test]
  public async Task RunAsync_AdvisoryAtTheDefaultThreshold_ExitsOneWithTheTableAsync() {
    using var workspace = new AuditWorkspace();

    var run = await _runAsync(["--project", _sampleProject(workspace)], _osvReporting("MODERATE"));

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_ADVISORIES);
    await Assert.That(run.Output).Contains("GHSA-cmd-0001 (CVE-2026-1001)  moderate  0.2616.0 or later");
    await Assert.That(run.Output).Contains("1 at or above moderate: the audit fails.");
  }

  [Test]
  [Arguments("LOW", null, AuditCommand.EXIT_CLEAN)]
  [Arguments("LOW", "low", AuditCommand.EXIT_ADVISORIES)]
  [Arguments("HIGH", "critical", AuditCommand.EXIT_CLEAN)]
  [Arguments("CRITICAL", "critical", AuditCommand.EXIT_ADVISORIES)]
  [Arguments("CRITICAL", "none", AuditCommand.EXIT_CLEAN)]
  [Arguments(null, "critical", AuditCommand.EXIT_ADVISORIES)]
  public async Task RunAsync_FailOn_SetsTheExitCodeAsync(string? severity, string? failOn, int expected) {
    // The default threshold is moderate, so a low advisory alone passes a default run. An
    // advisory with no severity fails even at critical.
    using var workspace = new AuditWorkspace();
    string[] args = failOn is null
      ? ["--project", _sampleProject(workspace)]
      : ["--project", _sampleProject(workspace), "--fail-on", failOn];

    var run = await _runAsync(args, _osvReporting(severity));

    await Assert.That(run.ExitCode).IsEqualTo(expected);
    await Assert.That(run.Output).Contains("GHSA-cmd-0001");
  }

  [Test]
  public async Task RunAsync_SeveralProjects_ChecksEveryVersionAnyOfThemResolvesAsync() {
    using var workspace = new AuditWorkspace();
    workspace.AddProject("src/SampleApp", "SampleApp", AuditWorkspace.Fixture("SampleApp"));
    workspace.AddProject("src/Worker", "Worker", AuditWorkspace.Fixture("Worker"));

    var run = await _runAsync(["-p", workspace.Root]);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_CLEAN);
    await Assert.That(run.Output).Contains("(6 packages checked)");
    var versions = JsonDocument.Parse(run.Osv.Requests[0].Body!).RootElement.GetProperty("queries").EnumerateArray()
      .Where(q => q.GetProperty("package").GetProperty("name").GetString() == CORE)
      .Select(q => q.GetProperty("version").GetString());
    await Assert.That(versions).IsEquivalentTo(new List<string?> { "0.2610.0", "0.2615.0" });
  }

  [Test]
  public async Task RunAsync_NotRestored_ExitsTwoAndNeverReportsNoAdvisoriesAsync() {
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("App", "App", assetsJson: null);

    var run = await _runAsync(["--project", project]);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_NOT_CHECKED);
    await Assert.That(run.Error).Contains("Run dotnet restore first");
    await Assert.That(run.Output).IsEmpty();
    await Assert.That(run.Osv.Requests).IsEmpty();
  }

  [Test]
  [Arguments(HttpStatusCode.ServiceUnavailable)]
  [Arguments(HttpStatusCode.InternalServerError)]
  public async Task RunAsync_OsvAnswersWithAnError_ExitsTwoAndNeverReportsNoAdvisoriesAsync(HttpStatusCode status) {
    using var workspace = new AuditWorkspace();
    var osv = new StubOsvHandler { Respond = (_, _) => Task.FromResult(StubOsvHandler.Json("{}", status)) };

    var run = await _runAsync(["--project", _sampleProject(workspace)], osv);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_NOT_CHECKED);
    await Assert.That(run.Output).DoesNotContain(CLEAN_LINE);
    await Assert.That(run.Error).Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
  }

  [Test]
  public async Task RunAsync_OsvTooSlow_ExitsTwoAfterTheDefaultFifteenSecondsAsync() {
    // The handler moves the clock and then waits for as long as it is let; only the command's
    // deadline can end the wait.
    using var workspace = new AuditWorkspace();
    var time = new FakeTimeProvider();
    var osv = new StubOsvHandler {
      Respond = async (_, cancellationToken) => {
        time.Advance(TimeSpan.FromSeconds(15));
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return StubOsvHandler.Json("{}");
      },
    };

    var run = await _runAsync(["--project", _sampleProject(workspace)], osv, time);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_NOT_CHECKED);
    await Assert.That(run.Error).Contains("within 15 seconds");
    await Assert.That(run.Output).DoesNotContain(CLEAN_LINE);
  }

  [Test]
  public async Task RunAsync_Timeout_IsConfigurableAsync() {
    // Three seconds in, a three-second deadline has passed and the fifteen-second default has not.
    using var workspace = new AuditWorkspace();
    var time = new FakeTimeProvider();
    var osv = new StubOsvHandler {
      Respond = async (_, cancellationToken) => {
        time.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return StubOsvHandler.Json("{}");
      },
    };

    var run = await _runAsync(["--project", _sampleProject(workspace), "--timeout", "3"], osv, time);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_NOT_CHECKED);
    await Assert.That(run.Error).Contains("within 3 seconds");
  }

  [Test]
  public async Task RunAsync_UnexpectedFailure_ExitsTwoNotOneAsync() {
    // Exit code 1 means "advisories found". A crash is not that; it is a check that did not run.
    using var workspace = new AuditWorkspace();
    var osv = new StubOsvHandler { Respond = (_, _) => throw new InvalidOperationException("transport fault") };

    var run = await _runAsync(["--project", _sampleProject(workspace)], osv);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_NOT_CHECKED);
    await Assert.That(run.Error).Contains("transport fault");
    await Assert.That(run.Output).DoesNotContain(CLEAN_LINE);
  }

  [Test]
  public async Task RunAsync_CallerCancels_IsACancellationNotAResultAsync() {
    using var workspace = new AuditWorkspace();
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.ThrowsAsync<OperationCanceledException>(
      () => _runAsync(["--project", _sampleProject(workspace)], cancellationToken: canceled.Token));
  }

  [Test]
  public async Task RunAsync_Json_WritesOnlyTheJsonDocumentWithTheSameExitCodeAsync() {
    using var workspace = new AuditWorkspace();

    var run = await _runAsync(["--project", _sampleProject(workspace), "--json"], _osvReporting("HIGH"));

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_ADVISORIES);
    var json = JsonDocument.Parse(run.Output).RootElement;
    await Assert.That(json.GetProperty("packagesChecked").GetInt32()).IsEqualTo(5);
    await Assert.That(json.GetProperty("fails").GetBoolean()).IsTrue();
    await Assert.That(json.GetProperty("findings")[0].GetProperty("advisoryId").GetString()).IsEqualTo("GHSA-cmd-0001");
  }

  [Test]
  public async Task RunAsync_NoWhizbangPackages_ExitsZeroWithoutAskingOsvAsync() {
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("App", "App", AuditWorkspace.Assets(("net10.0", "Humanizer.Core/2.14.1", "package")));

    var run = await _runAsync(["--project", project]);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_CLEAN);
    await Assert.That(run.Output).Contains("uses no Whizbang packages");
    await Assert.That(run.Osv.Requests).IsEmpty();
  }

  [Test]
  public async Task RunAsync_RequestsCarryTheCliUserAgentAndNoCredentialsAsync() {
    using var workspace = new AuditWorkspace();

    var run = await _runAsync(["--project", _sampleProject(workspace)], _osvReporting("LOW"));

    await Assert.That(run.Osv.Requests.Count).IsEqualTo(2);
    foreach (var request in run.Osv.Requests) {
      await Assert.That(request.Headers.UserAgent.ToString()).IsEqualTo(AuditCommand.UserAgent);
      await Assert.That(request.Headers.Authorization).IsNull();
      await Assert.That(request.Uri.Host).IsEqualTo("api.osv.dev");
    }
  }

  [Test]
  public async Task UserAgent_IsWhizbangCliAndTheToolVersionAsync() {
    // The informational version can carry "+<commit>"; a User-Agent product version cannot.
    var version = typeof(AuditCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
      .InformationalVersion.Split('+')[0];

    await Assert.That(AuditCommand.UserAgent).IsEqualTo($"whizbang-cli/{version}");
  }

  [Test]
  [NotInParallel(nameof(Environment.CurrentDirectory))]
  public async Task RunAsync_NoProjectOption_AuditsTheCurrentDirectoryAsync() {
    using var workspace = new AuditWorkspace();
    _sampleProject(workspace);
    var previous = Environment.CurrentDirectory;
    Environment.CurrentDirectory = workspace.Root;
    try {
      var run = await _runAsync([]);

      await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_CLEAN);
      await Assert.That(run.Output).Contains("(5 packages checked)");
    } finally {
      Environment.CurrentDirectory = previous;
    }
  }

  [Test]
  [Arguments("--help")]
  [Arguments("-h")]
  public async Task RunAsync_Help_DescribesTheCommandAndExitsZeroAsync(string flag) {
    var run = await _runAsync([flag]);

    await Assert.That(run.ExitCode).IsEqualTo(0);
    await Assert.That(run.Output).Contains("Usage: whizbang audit");
    await Assert.That(run.Output).Contains("--fail-on");
    await Assert.That(run.Output).Contains("api.osv.dev");
    await Assert.That(run.Osv.Requests).IsEmpty();
  }

  [Test]
  [Arguments("--verbose", "Unknown option")]
  [Arguments("--project", "--project needs a value")]
  [Arguments("--fail-on", "--fail-on needs a value")]
  [Arguments("--timeout", "--timeout needs a value")]
  public async Task RunAsync_BadArguments_ExitTwoWithUsageAsync(string argument, string message) {
    var run = await _runAsync([argument]);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_NOT_CHECKED);
    await Assert.That(run.Error).Contains(message);
    await Assert.That(run.Error).Contains("whizbang audit --help");
    await Assert.That(run.Osv.Requests).IsEmpty();
  }

  [Test]
  [Arguments("medium")]
  [Arguments("unknown")]
  public async Task RunAsync_UnknownThreshold_ExitsTwoAsync(string failOn) {
    var run = await _runAsync(["--fail-on", failOn]);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_NOT_CHECKED);
    await Assert.That(run.Error).Contains("low, moderate, high, critical or none");
  }

  [Test]
  [Arguments("abc")]
  [Arguments("0")]
  [Arguments("-5")]
  public async Task RunAsync_TimeoutThatIsNotAPositiveNumberOfSeconds_ExitsTwoAsync(string timeout) {
    var run = await _runAsync(["--timeout", timeout]);

    await Assert.That(run.ExitCode).IsEqualTo(AuditCommand.EXIT_NOT_CHECKED);
    await Assert.That(run.Error).Contains("positive whole number of seconds");
  }

  [Test]
  public async Task RunAsync_RealTransport_StillFailsTheCheckBeforeAnyNetworkCallWhenNotRestoredAsync() {
    // The overload the CLI uses. With no restore output the command stops before creating a
    // connection, so this runs offline.
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("App", "App", assetsJson: null);
    using var output = new StringWriter();
    using var error = new StringWriter();

    var exitCode = await AuditCommand.RunAsync(["--project", project], output, error);

    await Assert.That(exitCode).IsEqualTo(AuditCommand.EXIT_NOT_CHECKED);
    await Assert.That(error.ToString()).Contains("Run dotnet restore first");
  }

  [Test]
  [Arguments(new[] { "audit", "--json" }, true)]
  [Arguments(new[] { "AUDIT", "-p", ".", "--json" }, true)]
  [Arguments(new[] { "audit" }, false)]
  [Arguments(new[] { "schema", "--json" }, false)]
  [Arguments(new string[0], false)]
  public async Task SuppressesBanner_OnlyForAnAuditWritingJsonAsync(string[] args, bool expected) {
    // The banner goes to standard output. In front of a JSON report it makes the report unparseable.
    await Assert.That(AuditCommand.SuppressesBanner(args)).IsEqualTo(expected);
  }
}
