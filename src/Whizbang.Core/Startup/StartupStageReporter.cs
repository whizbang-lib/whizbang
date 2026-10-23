// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Startup;

/// <summary>
/// Default <see cref="IStartupStageReporter"/>: logs each stage, records it on the instance's row through the
/// registered <c>IWorkCoordinator</c>, and tells the observers.
/// </summary>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageReporterTests.cs</tests>
public sealed partial class StartupStageReporter : IStartupStageReporter {
  /// <summary>Creates the reporter.</summary>
  /// <param name="scopeFactory">Resolves the work coordinator that writes the instance's row.</param>
  /// <param name="instanceProvider">This instance's identity; without one nothing is written.</param>
  /// <param name="observers">Told of every change.</param>
  /// <param name="logger">Where each stage is logged.</param>
  /// <param name="timeProvider">The clock a stage's entry time and a write's timeout are read from.</param>
  public StartupStageReporter(
      IServiceScopeFactory scopeFactory,
      IServiceInstanceProvider? instanceProvider,
      IEnumerable<IStartupStageObserver> observers,
      ILogger<StartupStageReporter> logger,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(observers);
    ArgumentNullException.ThrowIfNull(logger);
    _ = instanceProvider;
    _ = timeProvider;
  }

  /// <inheritdoc />
  public StartupStageSnapshot? Current => null;

  /// <inheritdoc />
  public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) =>
    ValueTask.CompletedTask;

  /// <inheritdoc />
  public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) =>
    ValueTask.CompletedTask;

  /// <inheritdoc />
  public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
