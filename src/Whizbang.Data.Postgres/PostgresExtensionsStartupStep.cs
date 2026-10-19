// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The <c>PostgresExtensions</c> startup step: after <c>Migrate</c>, on every instance, probes each
/// registered database and writes the startup report.
/// </summary>
/// <remarks>
/// Non-blocking: readiness never waits for it, and it runs inside the startup pipeline, which by design
/// cannot stop the host. The probe itself answers Unknown rather than throwing for a database it cannot
/// read, so the step completes either way.
/// </remarks>
/// <docs>operations/infrastructure/postgres-extensions#startup-report</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionsStartupStepTests.cs</tests>
public sealed class PostgresExtensionsStartupStep : IStartupStep {
#pragma warning disable CA1707
  /// <summary>The step's name in the startup pipeline.</summary>
  public const string STEP_NAME = "PostgresExtensions";
#pragma warning restore CA1707

  private readonly IPostgresCapabilities _capabilities;
  private readonly PostgresExtensionsOptions _options;
  private readonly ILogger<PostgresExtensionsStartupStep> _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="capabilities">The capabilities it refreshes.</param>
  /// <param name="options">Whether to probe at all.</param>
  /// <param name="logger">Where the report goes.</param>
  public PostgresExtensionsStartupStep(
      IPostgresCapabilities capabilities,
      IOptions<PostgresExtensionsOptions> options,
      ILogger<PostgresExtensionsStartupStep> logger) {
    ArgumentNullException.ThrowIfNull(capabilities);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(logger);
    _capabilities = capabilities;
    _options = options.Value;
    _logger = logger;
  }

  /// <inheritdoc />
  public StartupStepDescriptor Descriptor { get; } = new() {
    Name = STEP_NAME,
    DependsOn = [FrameworkStartupSteps.MIGRATE],
    Blocking = false,
  };

  /// <inheritdoc />
  public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
    if (!_options.ProbeOnStartup) {
      return new StartupStepReport(StartupStepOutcome.Skipped,
        "probe turned off (Whizbang:PostgresExtensions:ProbeOnStartup = false); every extension reads Unknown");
    }

    await _capabilities.RefreshAsync(cancellationToken).ConfigureAwait(false);
    foreach (var database in _capabilities.Databases) {
      PostgresExtensionReport.Log(_logger, database);
    }
    return new StartupStepReport(StartupStepOutcome.Completed, string.Join("; ", _capabilities.Databases.Select(d =>
      $"{d.Database}: {d.Extensions.Count(e => e.IsUsable)} of {d.Extensions.Count} usable")));
  }
}
