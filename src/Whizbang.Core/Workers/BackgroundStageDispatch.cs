namespace Whizbang.Core.Workers;

/// <summary>
/// Scheduler for fire-and-forget background stage dispatch (PostLifecycle in
/// <see cref="PerspectiveWorker"/>, inbox Pre/PostDetached in <see cref="WorkCoordinatorPublisherWorker"/>).
/// </summary>
/// <remarks>
/// Uses <see cref="TaskCreationOptions.LongRunning"/> so the background work runs on a dedicated
/// thread rather than a pooled worker. This prevents ThreadPool starvation when multiple stage-
/// dispatch call sites are active under load: each site would otherwise contend for the same pool
/// with EF async continuations, transport callbacks, and other pipeline work. Observed symptom
/// before this helper: CI-bound integration tests saw Inbox*Detached stages sit queued past a
/// 120s deadline while PostLifecycle's <c>Task.Run</c>-scheduled background work monopolized
/// the pool.
/// </remarks>
public static class BackgroundStageDispatch {
  /// <summary>
  /// Schedules <paramref name="body"/> on a dedicated OS thread via
  /// <see cref="TaskFactory.StartNew(Action, CancellationToken, TaskCreationOptions, TaskScheduler)"/>
  /// with <see cref="TaskCreationOptions.LongRunning"/> + <see cref="TaskCreationOptions.DenyChildAttach"/>.
  /// The returned task completes when the async body completes (via <see cref="TaskExtensions.Unwrap(Task{Task})"/>).
  /// </summary>
  /// <remarks>
  /// <c>DenyChildAttach</c> isolates the body's internal task graph from the caller. <c>LongRunning</c>
  /// is the key contract: the runtime spins a dedicated thread instead of a pooled worker, which is
  /// how this helper differs from <see cref="Task.Run(System.Action, CancellationToken)"/>.
  /// </remarks>
  public static Task StartLongRunning(Func<Task> body, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(body);
    return Task.Factory.StartNew(
      body,
      cancellationToken,
      TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
      TaskScheduler.Default).Unwrap();
  }
}
