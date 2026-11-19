// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// Stops the host when the verdict finds an explicitly configured feature whose capability is known not to be
/// Ready: the instance exits through the one exit path (#2126) as a <see cref="ExitCause.StartupFailure"/> (exit code 4),
/// whose one entry lists every failing verdict line.
/// </summary>
/// <remarks>
/// <para>
/// It cannot run inside the startup pipeline, which by design never stops the host, and it cannot hold host
/// start while it waits: the probe runs after the schema is migrated, and a hosted service started after this
/// one may be what migrates it. So it waits for the verdict in the background from <see cref="StartAsync"/>.
/// </para>
/// <para>
/// The exit code is what makes the stop visible: an application stopped on request exits zero, which looks to
/// an orchestrator exactly like an orderly shutdown. The exit path sets it.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#validation</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Capabilities/CapabilityVerdictEnforcerTests.cs</tests>
internal sealed class CapabilityVerdictEnforcer : IHostedService, IDisposable {
  private readonly ICapabilityRegistry _registry;
  private readonly IWhizbangInstanceExit _instanceExit;
  private readonly CancellationTokenSource _stopping = new();

  /// <summary>Creates the enforcer.</summary>
  /// <param name="registry">The registry the verdict is published on.</param>
  /// <param name="instanceExit">The one exit path the enforcer takes when a required capability is not ready.</param>
  public CapabilityVerdictEnforcer(ICapabilityRegistry registry, IWhizbangInstanceExit instanceExit) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(instanceExit);
    _registry = registry;
    _instanceExit = instanceExit;
  }

  /// <summary>The background wait for the verdict and what it did about it; completed before start.</summary>
  internal Task Watching { get; private set; } = Task.CompletedTask;

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
    _instanceExit.RequestExit(new InstanceExitRequest(ExitCause.StartupFailure,
      $"{failures.Count} explicitly configured feature(s) need a capability that is not ready. Fix each, or remove the "
      + $"setting that turns the feature on:\n{lines}\n"));
  }
}
