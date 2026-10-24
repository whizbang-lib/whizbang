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
/// A key present in configuration overrides what code set; an absent key leaves it. Later slices add
/// <c>AutoProvision</c>, globally, per provider and per capability.
/// </remarks>
/// <docs>operations/infrastructure/capabilities#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityServiceCollectionExtensionsTests.cs</tests>
public sealed class CapabilityOptions {
  /// <summary>
  /// Whether the startup step probes every provider (<c>Whizbang:Capabilities:ProbeOnStartup</c>).
  /// Default <see langword="true"/>. When <see langword="false"/> the step is skipped and every capability
  /// reads <see cref="CapabilityState.Unknown"/> until something calls <see cref="ICapabilityRegistry.RefreshAsync"/>.
  /// </summary>
  public bool ProbeOnStartup { get; set; } = true;
}

/// <summary>
/// The <c>Capabilities</c> startup step: after <c>Migrate</c>, on every instance, probes every provider,
/// judges every requirement in force against the result, publishes the verdict on the registry and writes it.
/// </summary>
/// <remarks>
/// <para>
/// Non-blocking: readiness never waits for it, and it runs inside the startup pipeline, which by design
/// cannot stop the host. The registry reads a scope it cannot probe as Unknown, so the step completes
/// either way. A failing verdict stops the host from <see cref="CapabilityVerdictEnforcer"/>, outside the
/// pipeline.
/// </para>
/// <para>
/// With the probe turned off the verdict is still published, every capability Unknown, so an explicit
/// requirement warns rather than passing unseen.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#verdict</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilitiesStartupStepTests.cs</tests>
public sealed class CapabilitiesStartupStep : IStartupStep {
#pragma warning disable CA1707
  /// <summary>The step's name in the startup pipeline.</summary>
  public const string STEP_NAME = "Capabilities";
#pragma warning restore CA1707

  private readonly ICapabilityRegistry _registry;
  private readonly CapabilityOptions _options;
  private readonly IReadOnlyList<ICapabilityRequirementSource> _sources;
  private readonly IConfiguration? _configuration;
  private readonly ILogger<CapabilitiesStartupStep> _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="registry">The registry it refreshes and publishes the verdict on.</param>
  /// <param name="options">Whether to probe at all.</param>
  /// <param name="sources">Every source of declared requirements.</param>
  /// <param name="configuration">The host's configuration, which decides which requirements are in force; null when it has none.</param>
  /// <param name="logger">Where the verdict goes.</param>
  public CapabilitiesStartupStep(
      ICapabilityRegistry registry,
      IOptions<CapabilityOptions> options,
      IEnumerable<ICapabilityRequirementSource> sources,
      IConfiguration? configuration,
      ILogger<CapabilitiesStartupStep> logger) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(logger);
    _registry = registry;
    _options = options.Value;
    _sources = [.. sources];
    _configuration = configuration;
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
    if (_options.ProbeOnStartup) {
      await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
    var statuses = _registry.Statuses;
    var requirements = _sources.SelectMany(source => source.Requirements).Distinct();
    var verdict = CapabilityVerdict.Evaluate(statuses, CapabilityActivationResolver.Resolve(requirements, _configuration));
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
}
