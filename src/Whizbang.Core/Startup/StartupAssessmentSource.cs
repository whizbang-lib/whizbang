// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// The one <c>Assess</c> verdict this instance starts on (#1992). Assess is the first stage of startup: the schema
/// initializer asks for the verdict before it applies anything, the driver's schema pass reads it as its input, and the
/// <c>Assess</c> startup step reports it. All of them read it here, so the version record is read once and every reader
/// is given the same answer.
/// </summary>
/// <remarks>
/// <para>
/// A temporary database failure (<see cref="TransientDatabaseFailure"/>) is retried within Preflight, with the retry said
/// on the stage record. Any other failure fails the attempt loudly with a <see cref="StartupAssessmentException"/>, the
/// way a bootstrap that cannot be applied does: an instance that cannot read the version record never migrates blind. A
/// failure is never latched; the next attempt reads the record again.
/// </para>
/// <para>
/// It is an <see cref="IStartupAssessor"/> itself, so a reader takes it wherever an assessor is taken. The runtime
/// re-assessment (the standby watcher) reads the driver's assessor directly, because a verdict that was right at boot can
/// stop being right while the instance runs.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#assess</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupAssessmentSourceTests.cs</tests>
public sealed partial class StartupAssessmentSource : IStartupAssessor {
  private static readonly TimeSpan _maxRetryDelay = TimeSpan.FromSeconds(30);

  private readonly IStartupAssessor _assessor;
  private readonly IStartupStageReporter _stages;
  private readonly TimeProvider _time;
  private readonly ILogger _logger;
  private readonly Lock _gate = new();
  private Task<StartupAssessment>? _attempt;

  /// <summary>Creates the source over the driver's assessor.</summary>
  /// <param name="assessor">The driver's assessor; the framework's null default when no driver supplied one.</param>
  /// <param name="stages">Where a retry or a failure is said; <see cref="NullStartupStageReporter"/> when nothing records stages.</param>
  /// <param name="timeProvider">The clock a retry waits on.</param>
  /// <param name="logger">Where a retry or a failure is logged.</param>
  public StartupAssessmentSource(
      IStartupAssessor assessor, IStartupStageReporter stages, TimeProvider timeProvider, ILogger<StartupAssessmentSource> logger) {
    ArgumentNullException.ThrowIfNull(assessor);
    ArgumentNullException.ThrowIfNull(stages);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _assessor = assessor;
    _stages = stages;
    _time = timeProvider;
    _logger = logger ?? (ILogger)NullLogger.Instance;
  }

  /// <inheritdoc />
  public bool IsConfigured => _assessor.IsConfigured;

  /// <summary>The verdict, once it has been produced; <see langword="null"/> before then.</summary>
  public StartupAssessment? Verdict { get; private set; }

  /// <summary>
  /// The verdict: produced by the first call and handed to every later one. A call made while the first is reading
  /// waits for that read. A read canceled by its own caller leaves a caller still waiting free to read again.
  /// </summary>
  /// <param name="cancellationToken">Canceled when this caller stops waiting.</param>
  /// <returns>The one verdict.</returns>
  /// <exception cref="StartupAssessmentException">The version record could not be read for a reason that is not
  /// temporary.</exception>
  public async Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) {
    while (true) {
      Task<StartupAssessment>? attempt;
      TaskCompletionSource<StartupAssessment>? mine = null;
      lock (_gate) {
        if (_attempt is not { IsFaulted: false, IsCanceled: false }) {
          mine = new TaskCompletionSource<StartupAssessment>(TaskCreationOptions.RunContinuationsAsynchronously);
          _attempt = mine.Task;
        }
        attempt = _attempt;
      }

      if (mine is not null) {
        await _readAsync(mine, cancellationToken).ConfigureAwait(false);
      }

      try {
        return await attempt.WaitAsync(cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
        // The read belonged to another caller, who stopped waiting for it; this caller reads again.
      }
    }
  }

  /// <summary>How long the retry after the given number of temporary failures waits: one second, doubled, at most thirty.</summary>
  /// <param name="failures">The temporary failures so far, from one.</param>
  /// <returns>The delay.</returns>
  public static TimeSpan RetryDelay(int failures) {
    var seconds = Math.Pow(2, Math.Min(failures - 1, 5));
    var delay = TimeSpan.FromSeconds(seconds);
    return delay < _maxRetryDelay ? delay : _maxRetryDelay;
  }

  private async Task _readAsync(TaskCompletionSource<StartupAssessment> attempt, CancellationToken cancellationToken) {
    var failures = 0;
    while (true) {
      try {
        var verdict = await _assessor.AssessAsync(cancellationToken).ConfigureAwait(false);
        Verdict = verdict;
        attempt.SetResult(verdict);
        return;
      } catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested) {
        attempt.SetCanceled(ex.CancellationToken);
        return;
      } catch (Exception ex) when (TransientDatabaseFailure.TryClassify(ex, out var failure)) {
        failures++;
        // Scheduled before anyone is told, so a test driving the clock can advance it as soon as it hears.
        var delay = RetryDelay(failures);
        var wait = Task.Delay(delay, _time, cancellationToken);
        LogRetrying(_logger, ex, failure.Reason, failures, (long)delay.TotalMilliseconds);
        try {
          await _stages.EnterAsync(StartupStage.Preflight,
            $"the schema version record could not be read for a moment ({failure.Reason}); retrying", cancellationToken)
            .ConfigureAwait(false);
          await wait.ConfigureAwait(false);
        } catch (OperationCanceledException canceled) {
          attempt.SetCanceled(canceled.CancellationToken);
          return;
        }
      }
#pragma warning disable CA1031 // not swallowed: every other failure becomes the refusal the caller is handed
      catch (Exception ex) {
#pragma warning restore CA1031
        LogFailed(_logger, ex);
        var refusal = new StartupAssessmentException(ex);
        try {
          await _stages.EnterAsync(StartupStage.Preflight,
            "the schema version record could not be read; this attempt stops here rather than migrate blind",
            cancellationToken).ConfigureAwait(false);
        } finally {
          attempt.SetException(refusal);
        }
        return;
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "The schema version record could not be read ({Reason}); Assess retries in {DelayMs} ms (failure {Failures})")]
  static partial void LogRetrying(ILogger logger, Exception ex, string reason, int failures, long delayMs);

  [LoggerMessage(EventId = 2, Level = LogLevel.Error,
    Message = "The schema version record could not be read and the failure is not temporary; startup does not continue "
      + "past Preflight on this attempt, because an instance that cannot tell whether the schema is newer than its binary "
      + "must not migrate it")]
  static partial void LogFailed(ILogger logger, Exception ex);
}

/// <summary>Registers the one <see cref="StartupAssessmentSource"/> of a host (#1992).</summary>
/// <docs>operations/startup/rolling-upgrades#assess</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupAssessmentSourceTests.cs:TheDefaults_RegisterOneSourceOverTheRegisteredAssessor_AndTheAssessStepReadsThroughItAsync</tests>
public static class StartupAssessmentSourceServiceCollectionExtensions {
  /// <summary>
  /// Registers the source over whichever assessor the host registers (the framework's null default when none),
  /// resolved at first use so a driver registering after this call still wins. Idempotent: the defaults and the schema
  /// initializer both call it, and the Assess step and the initializer read one source.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection, for chaining.</returns>
  public static IServiceCollection TryAddStartupAssessmentSource(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddSingleton<IStartupAssessor>(NullStartupAssessor.Instance);
    services.TryAddSingleton(TimeProvider.System);
    services.TryAddSingleton(sp => new StartupAssessmentSource(
      sp.GetRequiredService<IStartupAssessor>(),
      sp.GetService<IStartupStageReporter>() ?? NullStartupStageReporter.Instance,
      sp.GetRequiredService<TimeProvider>(),
      sp.GetService<ILogger<StartupAssessmentSource>>()!));
    return services;
  }
}

/// <summary>
/// The version record could not be read for a reason that is not temporary (#1992). Fails the startup attempt in
/// Preflight, as a bootstrap that cannot be applied does, rather than let an instance migrate a schema it cannot place
/// against its own version.
/// </summary>
/// <docs>operations/startup/rolling-upgrades#assess</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupAssessmentSourceTests.cs:AnyOtherFailure_FailsStartupLoudly_AndIsNotLatchedAsync</tests>
public sealed class StartupAssessmentException : Exception {
  /// <summary>Creates the refusal with no cause.</summary>
  public StartupAssessmentException() : this(DEFAULT_MESSAGE) { }

  /// <summary>Creates the refusal with a message of the caller's.</summary>
  /// <param name="message">The message.</param>
  public StartupAssessmentException(string message) : base(message) { }

  /// <summary>Creates the refusal over what went wrong.</summary>
  /// <param name="innerException">Why the record could not be read.</param>
  public StartupAssessmentException(Exception innerException) : this(DEFAULT_MESSAGE, innerException) { }

  /// <summary>Creates the refusal with a message of the caller's over what went wrong.</summary>
  /// <param name="message">The message.</param>
  /// <param name="innerException">Why the record could not be read.</param>
  public StartupAssessmentException(string message, Exception innerException) : base(message, innerException) { }

  private const string DEFAULT_MESSAGE =
    "The schema version record could not be read, so this instance cannot tell whether the schema is newer than its "
    + "binary; startup stops in Preflight and never migrates blind. See the inner exception for what failed.";
}
