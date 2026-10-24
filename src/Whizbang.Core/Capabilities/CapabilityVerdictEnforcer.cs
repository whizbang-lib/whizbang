// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// Stops the host when the verdict finds an explicitly configured feature whose capability is known not to be
/// Ready: logs every failing verdict line at Critical, sets the process exit code to 1 and stops the application.
/// </summary>
/// <remarks>
/// <para>
/// It cannot run inside the startup pipeline, which by design never stops the host, and it cannot hold host
/// start while it waits: the probe runs after the schema is migrated, and a hosted service started after this
/// one may be what migrates it. So it waits for the verdict in the background from <see cref="StartAsync"/>.
/// </para>
/// <para>
/// The exit code is what makes the stop visible: an application stopped on request exits zero, which looks to
/// an orchestrator exactly like an orderly shutdown.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#validation</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Capabilities/CapabilityVerdictEnforcerTests.cs</tests>
internal sealed partial class CapabilityVerdictEnforcer : IHostedService, IDisposable {
  private readonly ICapabilityRegistry _registry;
  private readonly Action _stopApplication;
  private readonly ILogger<CapabilityVerdictEnforcer> _logger;
  private readonly Action<int> _setExitCode;
  private readonly CancellationTokenSource _stopping = new();

  /// <summary>Creates the enforcer.</summary>
  /// <param name="registry">The registry the verdict is published on.</param>
  /// <param name="stopApplication">
  /// Stops the application; <see cref="StopApplicationOf"/> in a host, which resolves the host's lifetime only when it
  /// is called, so a container without a host still resolves this service.
  /// </param>
  /// <param name="logger">Where the failing lines go.</param>
  /// <param name="setExitCode">Sets the process exit code; <see cref="SetProcessExitCode"/> in a host.</param>
  public CapabilityVerdictEnforcer(
      ICapabilityRegistry registry,
      Action stopApplication,
      ILogger<CapabilityVerdictEnforcer> logger,
      Action<int> setExitCode) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(stopApplication);
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(setExitCode);
    _registry = registry;
    _stopApplication = stopApplication;
    _logger = logger;
    _setExitCode = setExitCode;
  }

  /// <summary>The background wait for the verdict and what it did about it; completed before start.</summary>
  internal Task Watching { get; private set; } = Task.CompletedTask;

  /// <summary>Stops the host's application, resolving its lifetime from <paramref name="services"/> when called.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The stop action.</returns>
  internal static Action StopApplicationOf(IServiceProvider services) =>
    () => services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

  /// <summary>Sets <see cref="Environment.ExitCode"/>.</summary>
  /// <param name="code">The exit code.</param>
  internal static void SetProcessExitCode(int code) => Environment.ExitCode = code;

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    Watching = _watchAsync(_stopping.Token);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public async Task StopAsync(CancellationToken cancellationToken) {
    await _stopping.CancelAsync().ConfigureAwait(false);
    await Watching.ConfigureAwait(false);
  }

  /// <inheritdoc />
  public void Dispose() => _stopping.Dispose();

  private async Task _watchAsync(CancellationToken stopping) {
    CapabilityVerdict verdict;
    try {
      verdict = await _registry.WaitForVerdictAsync(stopping).ConfigureAwait(false);
    } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
      return;   // the host stopped before the probe finished: nothing was judged
    }
    var failures = verdict.Failures;
    if (failures.Count == 0) {
      return;
    }
    // Built once, on the way to stopping the host, so the cost does not depend on the log level.
    var lines = string.Join('\n', failures.Select(entry => "  " + entry.Line));
    LogStopping(_logger, failures.Count, lines);
    _setExitCode(1);
    _stopApplication();
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Critical,
    Message = "Stopping: {Count} explicitly configured feature(s) need a capability that is not ready. Fix each, or remove the setting that turns the feature on\n{Lines}")]
  static partial void LogStopping(ILogger logger, int count, string lines);
}
