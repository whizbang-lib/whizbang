namespace Whizbang.Core.Workers;

/// <summary>
/// What a long-running worker loop does with an exception that came out of one iteration: name what
/// it was, wait a bounded while, and go on.
/// </summary>
/// <remarks>
/// <para>
/// A loop that lets an exception out of <c>ExecuteAsync</c> stops the host, because the default
/// <c>HostOptions.BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>. That is the right
/// default for a worker that cannot run at all, and the wrong outcome for a deadlock against a
/// sibling instance's schema DDL, which the next attempt would win: an instance leaves a fleet for
/// the length of a restart over a failure that had already passed. Every loop therefore catches per
/// iteration and asks this what it caught — a <see cref="TransientDatabaseFailure"/> is reported
/// with its reason, anything else is reported as the defect it is — and continues either way,
/// because a stopped host reports nothing at all.
/// </para>
/// <para>
/// The wait grows from <see cref="DEFAULT_FLOOR"/> toward <see cref="DEFAULT_CEILING"/> while
/// failures keep coming and snaps back to the floor the moment an iteration succeeds
/// (<see cref="Recovered"/>), so a database that is unreachable is asked about once a ceiling rather
/// than as fast as the loop can spin. It is taken on the caller's <see cref="TimeProvider"/>, so a
/// test drives the backoff instead of living through it. One instance may be shared by several loops
/// of the same worker: the cadence then belongs to the worker rather than to each loop.
/// </para>
/// <para>
/// The reports are the caller's own <c>LoggerMessage</c> methods, passed in, so each worker keeps its
/// own event ids and wording while the decision of which one to use lives here and only here.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/transient-database-failures</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkerLoopRecoveryTests.cs</tests>
public sealed class WorkerLoopRecovery {
#pragma warning disable CA1707 // Repo style: public const/static-readonly fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>The wait after the first failure of a run.</summary>
  public static readonly TimeSpan DEFAULT_FLOOR = TimeSpan.FromMilliseconds(250);

  /// <summary>The longest wait a run of failures reaches.</summary>
  public static readonly TimeSpan DEFAULT_CEILING = TimeSpan.FromSeconds(30);
#pragma warning restore CA1707

  private readonly TimeProvider _timeProvider;
  private readonly AdaptiveIdleBackoff _backoff;
  private readonly Lock _gate = new();

  /// <summary>Creates a recovery with the default backoff bounds.</summary>
  /// <param name="timeProvider">The clock the backoff is waited on.</param>
  public WorkerLoopRecovery(TimeProvider timeProvider)
    : this(timeProvider, DEFAULT_FLOOR, DEFAULT_CEILING) { }

  /// <summary>Creates a recovery with explicit backoff bounds.</summary>
  /// <param name="timeProvider">The clock the backoff is waited on.</param>
  /// <param name="floor">The wait after the first failure of a run.</param>
  /// <param name="ceiling">The longest wait a run of failures reaches.</param>
  public WorkerLoopRecovery(TimeProvider timeProvider, TimeSpan floor, TimeSpan ceiling) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    _timeProvider = timeProvider;
    _backoff = new AdaptiveIdleBackoff(floor, ceiling);
  }

  /// <summary>How long the next failure will wait.</summary>
  public TimeSpan NextBackoff {
    get { lock (_gate) { return _backoff.Current; } }
  }

  /// <summary>
  /// Reports <paramref name="exception"/> through whichever of the two reports fits what it is.
  /// </summary>
  /// <remarks>
  /// For a loop that already has a cadence of its own and only needs the classification. A loop with
  /// no cadence of its own uses <see cref="RecoverAsync"/>, which adds the wait.
  /// </remarks>
  /// <param name="exception">The exception the loop body threw.</param>
  /// <param name="reportTransient">Reports a database failure that passes, with its classification.</param>
  /// <param name="reportDefect">Reports anything else, which is a defect.</param>
  public static void Report(
      Exception exception,
      Action<TransientDatabaseFailure, Exception> reportTransient,
      Action<Exception> reportDefect) {
    ArgumentNullException.ThrowIfNull(exception);
    ArgumentNullException.ThrowIfNull(reportTransient);
    ArgumentNullException.ThrowIfNull(reportDefect);

    if (TransientDatabaseFailure.TryClassify(exception, out var transient)) {
      reportTransient(transient, exception);
    } else {
      reportDefect(exception);
    }
  }

  /// <summary>
  /// Reports <paramref name="exception"/> and waits the loop's backoff, so the caller can continue.
  /// </summary>
  /// <param name="exception">The exception the loop body threw.</param>
  /// <param name="reportTransient">Reports a database failure that passes, with its classification.</param>
  /// <param name="reportDefect">Reports anything else, which is a defect.</param>
  /// <param name="cancellationToken">The loop's stopping token; cancels the wait.</param>
  /// <returns>A task that completes when the loop may try again.</returns>
  public Task RecoverAsync(
      Exception exception,
      Action<TransientDatabaseFailure, Exception> reportTransient,
      Action<Exception> reportDefect,
      CancellationToken cancellationToken) {
    Report(exception, reportTransient, reportDefect);

    TimeSpan wait;
    lock (_gate) {
      wait = _backoff.Next(foundWork: false);
    }

    return Task.Delay(wait, _timeProvider, cancellationToken);
  }

  /// <summary>Snaps the backoff back to its floor: an iteration succeeded.</summary>
  public void Recovered() {
    lock (_gate) {
      _backoff.Reset();
    }
  }
}
