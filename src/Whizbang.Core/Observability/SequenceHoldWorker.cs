// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Observability;

/// <summary>
/// Measures, on every instance, how long the oldest outbox event it holds for its commit-order stamp has waited
/// (#2542), publishes it as <see cref="SequenceHoldMetrics.OLDEST_AGE_SECONDS"/>, and logs a warning once it passes
/// <see cref="WhizbangCoreOptions.SequenceHoldWarningAge"/>.
/// </summary>
/// <remarks>
/// A bounded hold normally ends within milliseconds, when the stamper rings the outbox. A hold until stamped waits as
/// long as stamping takes, which under a bulk load can be hours; this reading is how that shows. It reads only this
/// instance's holdings, through a bounded walk of <see cref="MAX_ROWS"/> rows
/// (<see cref="IWorkCoordinator.GetOldestSequenceHeldOutboxAsync"/>), every
/// <see cref="WhizbangCoreOptions.SequenceHoldSampleInterval"/>; zero or less turns it off.
/// </remarks>
/// <docs>fundamentals/messaging/sequence-integrity#watching-held-messages</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Observability/SequenceHoldWorkerTests.cs</tests>
#pragma warning disable IDE0052, S4487, RCS1163, IDE0060, CS0067, S1144, RCS1213, CA1823, S2589 // RED stub (#2542)
public sealed partial class SequenceHoldWorker : BackgroundService, IDbMechanismOwner {
  /// <summary>The most pending rows one reading walks.</summary>
  public const int MAX_ROWS = 1000;

  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly WhizbangCoreOptions _options;
  private readonly SequenceHoldMetrics _metrics;
  private readonly ILogger<SequenceHoldWorker> _logger;
  private readonly TimeProvider _time;

  /// <summary>Creates the worker.</summary>
  /// <param name="scopeFactory">Scopes the work coordinator is resolved from.</param>
  /// <param name="instanceProvider">This instance's identity, whose holdings are read.</param>
  /// <param name="options">The interval and the warning age.</param>
  /// <param name="metrics">The gauge the reading is published to.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="timeProvider">Optional clock; the system clock when null.</param>
  /// <exception cref="ArgumentNullException">Thrown when a required dependency is null.</exception>
  public SequenceHoldWorker(
      IServiceScopeFactory scopeFactory,
      IServiceInstanceProvider instanceProvider,
      WhizbangCoreOptions options,
      SequenceHoldMetrics metrics,
      ILogger<SequenceHoldWorker> logger,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(instanceProvider);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(metrics);
    ArgumentNullException.ThrowIfNull(logger);
    _scopeFactory = scopeFactory;
    _instanceProvider = instanceProvider;
    _options = options;
    _metrics = metrics;
    _logger = logger;
    _time = timeProvider ?? TimeProvider.System;
  }

  /// <inheritdoc />
  public DbMechanism DbMechanism => DbMechanism.Outbox;

  /// <summary>Fires after each reading with the age it found: zero when nothing is held, null when it is unknown.</summary>
  public event Action<TimeSpan?>? Sampled;

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

  /// <summary>
  /// Takes one reading and publishes it. Public so a test drives a single reading instead of racing the timer.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The oldest held event's age, zero when none is held, or null when it could not be read.</returns>
  public Task<TimeSpan?> SampleOnceAsync(CancellationToken cancellationToken) {
    Sampled?.Invoke(null);
    return Task.FromResult<TimeSpan?>(null);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "The oldest outbox event this instance holds for its commit-order stamp has waited {AgeSeconds} s, past the {WarningSeconds} s warning age; the stamper is behind or not running")]
  private static partial void LogHeldTooLong(ILogger logger, double ageSeconds, double warningSeconds);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Could not read the age of the oldest outbox event held for its commit-order stamp; the next interval retries")]
  private static partial void LogReadFailed(ILogger logger, Exception exception);
}
#pragma warning restore IDE0052, S4487, RCS1163, IDE0060, CS0067, S1144, RCS1213, CA1823, S2589
