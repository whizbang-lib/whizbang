// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Startup;

#pragma warning disable S2325, CA1822, IDE0060 // RED stub, replaced by the implementation

/// <summary>
/// The one way a start runs its heavy startup work (#2596): each unit is a declared name, per-schema-per-build work
/// runs once per schema per build, and an instance holds one of the fleet's heavy-start permits while any of its heavy
/// work runs.
/// </summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/StartupWorkSessionTests.cs</tests>
public sealed partial class StartupWorkSession : IAsyncDisposable {
  /// <summary>Creates a session for one start.</summary>
  /// <param name="ledger">The database's side; null runs every unit directly, as before the cap existed.</param>
  /// <param name="instanceId">This instance; null runs every unit directly, since nothing can be claimed for it.</param>
  /// <param name="options">The cap.</param>
  /// <param name="passApplied">Whether this start's own schema pass applied anything, so shared work runs again.</param>
  /// <param name="stages">Where the wait for a permit is reported; null reports nothing.</param>
  /// <param name="timeProvider">The clock the wait is measured and paced by.</param>
  /// <param name="logger">Where skips, waits and failures are said; null for silence.</param>
  public StartupWorkSession(
      IStartupWorkLedger? ledger, Guid? instanceId, StartupWorkOptions options, bool passApplied,
      IStartupStageReporter? stages, TimeProvider timeProvider, ILogger? logger) {
    throw new NotImplementedException();
  }

  /// <summary>
  /// How the wait between two permit requests is spent; by default a delay on the session's clock. A test replaces it so
  /// the wait is a signal it controls rather than a timer it races.
  /// </summary>
  internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = null!;

  /// <summary>Whether this session holds a heavy-start permit now.</summary>
  public bool HoldsPermit => throw new NotImplementedException();

  /// <summary>Runs the declared work <paramref name="name"/> under its scope's rule.</summary>
  /// <param name="name">A name <see cref="FrameworkStartupWork"/> declares.</param>
  /// <param name="inputs">What this build gives the work; with the library version, its fingerprint for this build.</param>
  /// <param name="work">The work; returns whether it completed, so an unfinished run releases its claim.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How it ended.</returns>
  /// <exception cref="InvalidOperationException">Nothing declares <paramref name="name"/>.</exception>
  public Task<StartupWorkOutcome> RunAsync(
      string name, string inputs, Func<CancellationToken, Task<bool>> work, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <summary>The fingerprint of <paramref name="name"/> for <paramref name="inputs"/> under this library version.</summary>
  /// <param name="name">The declared name.</param>
  /// <param name="inputs">What this build gives the work.</param>
  /// <returns>Lowercase hex SHA-256.</returns>
  public static string Fingerprint(string name, string inputs) => throw new NotImplementedException();

  /// <summary>Gives the permit back, if this session holds one.</summary>
  /// <returns>A task that completes once the permit is released.</returns>
  public ValueTask DisposeAsync() => throw new NotImplementedException();
}
