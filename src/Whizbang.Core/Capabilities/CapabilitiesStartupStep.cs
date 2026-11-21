// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// Settings for capability detection, bound from <c>Whizbang:Capabilities</c>.
/// </summary>
/// <remarks>
/// A key present in configuration overrides what code set; an absent key leaves it. Provisioning is also switched
/// per provider and per capability, by keys read where they apply (<see cref="CapabilityAutoProvision"/>).
/// </remarks>
/// <docs>operations/infrastructure/capabilities#configuration</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Capabilities/CapabilityServiceCollectionExtensionsTests.cs</tests>
// Renamed when extension detection became the generic capability pattern.
[Whizbang.Core.Configuration.RemovedConfigurationKey("Whizbang:PostgresExtensions:ProbeOnStartup", "Whizbang:Capabilities:ProbeOnStartup")]
public sealed class CapabilityOptions {
  /// <summary>
  /// Whether the startup step probes every provider (<c>Whizbang:Capabilities:ProbeOnStartup</c>).
  /// Default <see langword="true"/>. When <see langword="false"/> the step is skipped and every capability
  /// reads <see cref="CapabilityState.Unknown"/> until something calls <see cref="ICapabilityRegistry.RefreshAsync"/>.
  /// </summary>
  public bool ProbeOnStartup { get; set; } = true;

  /// <summary>
  /// Whether the library creates a required capability that is absent and upgrades one with a newer version
  /// available (<c>Whizbang:Capabilities:AutoProvision</c>). Default <see langword="true"/>. The global level of a
  /// three-level switch: <c>Whizbang:Capabilities:&lt;provider&gt;:AutoProvision</c> and
  /// <c>Whizbang:Capabilities:&lt;provider&gt;:&lt;name&gt;:AutoProvision</c> override it. Turned off, a capability an
  /// operator provisioned is still detected and used.
  /// </summary>
  public bool AutoProvision { get; set; } = true;
}

/// <summary>
/// The <c>Capabilities</c> startup step: after <c>Migrate</c>, on every instance, probes every provider, provisions
/// every required capability the switch allows, judges every requirement in force against the result, publishes
/// the verdict on the registry and writes it.
/// </summary>
/// <remarks>
/// <para>
/// Non-blocking: readiness never waits for it, and it runs inside the startup pipeline, which by design
/// cannot stop the host. The registry reads a scope it cannot probe as Unknown, so the step completes
/// either way. A failing verdict stops the host from <see cref="CapabilityVerdictEnforcer"/>, outside the
/// pipeline.
/// </para>
/// <para>
/// While an explicit requirement in force reads <see cref="CapabilityState.Unknown"/> after the probe, the step
/// probes again, at most <see cref="UNKNOWN_RETRIES"/> times, waiting
/// <see cref="StartupAssessmentSource.RetryDelay"/> before each (1, 2 and 4 seconds). One still Unknown after the
/// last retry fails start (#1513, decided in #1672). An implicit requirement is never waited for, and nothing is
/// retried for a capability no provider describes. The step does not block readiness, so the wait holds nothing back.
/// </para>
/// <para>
/// Provisioning runs between the probe and the verdict, so a required capability the library creates is Ready
/// when it is judged. Only what a requirement in force names is provisioned; everything else is only detected.
/// </para>
/// <para>
/// With the probe turned off the verdict is still published, every capability Unknown, so an explicit
/// requirement warns rather than passing unseen; it does not fail start, because nothing probed it. Nothing is
/// provisioned on a guess.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#verdict</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Capabilities/CapabilitiesStartupStepTests.cs</tests>
public sealed partial class CapabilitiesStartupStep : IStartupStep {
#pragma warning disable CA1707
  /// <summary>The step's name in the startup pipeline.</summary>
  public const string STEP_NAME = "Capabilities";

  /// <summary>How many times the step probes again while an explicit requirement reads Unknown: the retry's bound.</summary>
  public const int UNKNOWN_RETRIES = 3;
#pragma warning restore CA1707

  private readonly ICapabilityRegistry _registry;
  private readonly CapabilityOptions _options;
  private readonly IReadOnlyList<ICapabilityRequirementSource> _sources;
  private readonly IReadOnlyList<ICapabilityProvisioner> _provisioners;
  private readonly IConfiguration? _configuration;
  private readonly ILogger<CapabilitiesStartupStep> _logger;
  private readonly TimeProvider _time;

  /// <summary>Creates the step.</summary>
  /// <param name="registry">The registry it refreshes and publishes the verdict on.</param>
  /// <param name="options">Whether to probe at all.</param>
  /// <param name="sources">Every source of declared requirements.</param>
  /// <param name="provisioners">Every provider's provisioner; a provider without one is only detected.</param>
  /// <param name="configuration">The host's configuration, which decides which requirements are in force; null when it has none.</param>
  /// <param name="logger">Where the verdict goes.</param>
  public CapabilitiesStartupStep(
      ICapabilityRegistry registry,
      IOptions<CapabilityOptions> options,
      IEnumerable<ICapabilityRequirementSource> sources,
      IEnumerable<ICapabilityProvisioner> provisioners,
      IConfiguration? configuration,
      ILogger<CapabilitiesStartupStep> logger)
    : this(registry, options, sources, provisioners, configuration, logger, TimeProvider.System) {
  }

  /// <summary>Creates the step with the clock its retries wait on.</summary>
  /// <param name="registry">The registry it refreshes and publishes the verdict on.</param>
  /// <param name="options">Whether to probe at all.</param>
  /// <param name="sources">Every source of declared requirements.</param>
  /// <param name="provisioners">Every provider's provisioner; a provider without one is only detected.</param>
  /// <param name="configuration">The host's configuration, which decides which requirements are in force; null when it has none.</param>
  /// <param name="logger">Where the verdict goes.</param>
  /// <param name="timeProvider">The clock a retry waits on.</param>
  public CapabilitiesStartupStep(
      ICapabilityRegistry registry,
      IOptions<CapabilityOptions> options,
      IEnumerable<ICapabilityRequirementSource> sources,
      IEnumerable<ICapabilityProvisioner> provisioners,
      IConfiguration? configuration,
      ILogger<CapabilitiesStartupStep> logger,
      TimeProvider timeProvider) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(provisioners);
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _registry = registry;
    _options = options.Value;
    _sources = [.. sources];
    _provisioners = [.. provisioners];
    _configuration = configuration;
    _logger = logger;
    _time = timeProvider;
  }

  /// <inheritdoc />
  public StartupStepDescriptor Descriptor { get; } = new() {
    Name = STEP_NAME,
    DependsOn = [FrameworkStartupSteps.MIGRATE],
    Blocking = false,
  };

  /// <inheritdoc />
  public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
    var inForce = _sources.SelectMany(source => source.Resolve(_configuration)).Distinct().ToList();
    if (_options.ProbeOnStartup) {
      await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
      for (var retry = 1; retry <= UNKNOWN_RETRIES && _explicitlyUnknown(inForce) is { } unknown; retry++) {
        // Scheduled before it is logged, so a test driving the clock can advance it as soon as it hears.
        var delay = StartupAssessmentSource.RetryDelay(retry);
        var wait = Task.Delay(delay, _time, cancellationToken);
        LogProbingAgain(_logger, unknown, (long)delay.TotalMilliseconds, retry, UNKNOWN_RETRIES);
        await wait.ConfigureAwait(false);
        await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
      }
      _registry.Record(await CapabilityProvisioningRunner.RunAsync(
        _registry.Statuses, inForce, _provisioners, _options, _configuration, _logger, cancellationToken).ConfigureAwait(false));
    }
    var statuses = _registry.Statuses;
    var verdict = CapabilityVerdict.Evaluate(statuses, inForce, probed: _options.ProbeOnStartup);
    _registry.PublishVerdict(verdict);
    CapabilityReport.LogVerdict(_logger, verdict);

    if (!_options.ProbeOnStartup) {
      return new StartupStepReport(StartupStepOutcome.Skipped,
        "probe turned off (Whizbang:Capabilities:ProbeOnStartup = false); every capability reads Unknown");
    }
    CapabilityReport.Log(_logger, [.. statuses.Where(status => !verdict.IsRequired(status))]);
    return new StartupStepReport(StartupStepOutcome.Completed,
      $"{statuses.Count(s => s.IsUsable)} of {statuses.Count} usable");
  }

  // The capabilities an explicit requirement in force would fail start on only because a provider reads them
  // Unknown, named with their scope; null when there are none.
  private string? _explicitlyUnknown(List<ActiveCapabilityRequirement> inForce) {
    var unknown = CapabilityVerdict.Evaluate(_registry.Statuses, inForce, probed: true).Failures
      .Where(entry => entry.Status.State == CapabilityState.Unknown)
      .Select(entry => $"{entry.Status.Descriptor.Id} on '{entry.Status.Scope}'")
      .ToList();
    return unknown.Count == 0 ? null : string.Join(", ", unknown);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Explicitly required capabilities read Unknown ({Capabilities}); probing again in {DelayMs} ms (retry {Retry} of {Retries}). "
      + "Still Unknown after the last retry fails start")]
  static partial void LogProbingAgain(ILogger logger, string capabilities, long delayMs, int retry, int retries);
}
