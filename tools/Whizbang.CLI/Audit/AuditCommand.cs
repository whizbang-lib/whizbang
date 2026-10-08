// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;

namespace Whizbang.CLI.Audit;

/// <summary>
/// <c>whizbang audit</c>: checks the Whizbang package versions a project resolves against the
/// published security advisories in OSV, and sets the exit code for CI.
/// </summary>
/// <remarks>
/// An on-demand command only. Nothing in the Whizbang libraries checks for advisories at startup or
/// at run time; a build that wants the check runs this command.
/// </remarks>
/// <docs>tools/cli-audit</docs>
internal static class AuditCommand {
  /// <summary>No advisory at or above <c>--fail-on</c>.</summary>
  public const int EXIT_CLEAN = 0;

  /// <summary>At least one advisory at or above <c>--fail-on</c>.</summary>
  public const int EXIT_ADVISORIES = 1;

  /// <summary>The check could not be completed; nothing is known about the packages.</summary>
  public const int EXIT_NOT_CHECKED = 2;

  private const int DEFAULT_TIMEOUT_SECONDS = 15;

  /// <summary>The User-Agent sent to OSV: <c>whizbang-cli/&lt;version&gt;</c>.</summary>
  /// <remarks>The informational version can end in <c>+&lt;commit&gt;</c>, which a User-Agent product version cannot carry.</remarks>
  public static string UserAgent { get; } = "whizbang-cli/" + typeof(AuditCommand).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

  /// <summary>
  /// Whether the command line asks for <c>audit --json</c>, whose standard output must be the JSON
  /// document alone, so the CLI's banner is not printed.
  /// </summary>
  /// <param name="args">The full command line.</param>
  /// <returns>True for an audit that writes JSON.</returns>
  public static bool SuppressesBanner(string[] args) =>
    args is [var command, ..] && string.Equals(command, "audit", StringComparison.OrdinalIgnoreCase) && args.Contains("--json");

  /// <summary>
  /// Runs the command against the real OSV service.
  /// </summary>
  /// <param name="args">The arguments after <c>audit</c>.</param>
  /// <param name="output">Where the report goes.</param>
  /// <param name="error">Where failures to check go.</param>
  /// <param name="cancellationToken">Cancels the check.</param>
  /// <returns>The exit code: 0, 1 or 2.</returns>
  public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default) =>
    RunAsync(args, output, error, () => new SocketsHttpHandler(), TimeProvider.System, cancellationToken);

  /// <summary>
  /// Runs the command with an injected transport and clock.
  /// </summary>
  /// <param name="args">The arguments after <c>audit</c>.</param>
  /// <param name="output">Where the report goes.</param>
  /// <param name="error">Where failures to check go.</param>
  /// <param name="createHandler">Creates the transport to OSV, only once there is something to ask.</param>
  /// <param name="timeProvider">The clock the deadline runs on.</param>
  /// <param name="cancellationToken">Cancels the check.</param>
  /// <returns>The exit code: 0, 1 or 2.</returns>
  public static async Task<int> RunAsync(
      string[] args,
      TextWriter output,
      TextWriter error,
      Func<HttpMessageHandler> createHandler,
      TimeProvider timeProvider,
      CancellationToken cancellationToken) {
    var options = _parse(args, out var usageError);
    if (options is null) {
      await error.WriteLineAsync($"whizbang audit: {usageError}");
      await error.WriteLineAsync("Run 'whizbang audit --help' for usage.");
      return EXIT_NOT_CHECKED;
    }

    if (options.Help) {
      await output.WriteAsync(HELP);
      return EXIT_CLEAN;
    }

    try {
      var packages = ProjectAssetsReader.ReadWhizbangPackages(options.Project);
      IReadOnlyList<OsvMatch> matches = [];
      if (packages.Count > 0) {
        using var osv = new OsvClient(createHandler(), UserAgent, options.Timeout, timeProvider);
        matches = await osv.FindAdvisoriesAsync(packages, cancellationToken);
      }

      var report = AuditReport.Build(packages, matches, options.FailOn);
      await output.WriteAsync(options.Json
        ? AuditReportFormatter.FormatJson(report) + Environment.NewLine
        : AuditReportFormatter.FormatText(report));
      return report.Failing.Count > 0 ? EXIT_ADVISORIES : EXIT_CLEAN;
    } catch (AuditCheckException ex) {
      await error.WriteLineAsync($"Could not check for advisories: {ex.Message}");
      return EXIT_NOT_CHECKED;
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Exit code 1 means "advisories found". An unexpected failure is a check that did not run,
      // and a CI step must not read it as anything else.
      await error.WriteLineAsync($"Could not check for advisories: unexpected {ex.GetType().Name}: {ex.Message}");
      return EXIT_NOT_CHECKED;
    }
  }

  private sealed record Options(string Project, AdvisorySeverity? FailOn, TimeSpan Timeout, bool Json, bool Help);

  private static Options? _parse(string[] args, out string? usageError) {
    var options = new Options(Environment.CurrentDirectory, AdvisorySeverity.Moderate, TimeSpan.FromSeconds(DEFAULT_TIMEOUT_SECONDS), Json: false, Help: false);
    usageError = null;
    var remaining = new Queue<string>(args);
    while (remaining.TryDequeue(out var arg)) {
      if (arg is "--help" or "-h") {
        return options with { Help = true };
      }

      if (arg == "--json") {
        options = options with { Json = true };
        continue;
      }

      if (arg is not ("--project" or "-p" or "--fail-on" or "--timeout")) {
        usageError = $"Unknown option '{arg}'.";
        return null;
      }

      if (!remaining.TryDequeue(out var value)) {
        usageError = $"{arg} needs a value.";
        return null;
      }

      switch (arg) {
        case "--fail-on":
          if (!AdvisorySeverities.TryParseThreshold(value, out var threshold)) {
            usageError = $"--fail-on must be low, moderate, high, critical or none, not '{value}'.";
            return null;
          }
          options = options with { FailOn = threshold };
          break;
        case "--timeout":
          if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds == 0) {
            usageError = $"--timeout must be a positive whole number of seconds, not '{value}'.";
            return null;
          }
          options = options with { Timeout = TimeSpan.FromSeconds(seconds) };
          break;
        default:
          options = options with { Project = value };
          break;
      }
    }

    return options;
  }

  private const string HELP = """
    Audit Command

    Usage: whizbang audit [options]

    Checks the Whizbang package versions a project resolves (transitive ones included) against the
    published security advisories in the OSV database (https://api.osv.dev), the same data NuGet
    Audit and Dependabot read. Run it after dotnet restore. Requests are anonymous.

    Options:
      --project, -p <path>   A project file, a .sln or .slnx solution, or a directory searched
                             for projects (default: the current directory)
      --fail-on <level>      low | moderate | high | critical | none (default: moderate)
      --timeout <seconds>    Give up on OSV after this long (default: 15)
      --json                 Write the report as JSON
      --help, -h             Show this help

    Exit codes:
      0  No advisory at or above --fail-on
      1  At least one advisory at or above --fail-on
      2  Could not check: no restore output, OSV unreachable or too slow, or bad arguments

    Examples:
      whizbang audit
      whizbang audit --project ./MyService.slnx --fail-on high
      whizbang audit --json > audit.json

    """;
}
