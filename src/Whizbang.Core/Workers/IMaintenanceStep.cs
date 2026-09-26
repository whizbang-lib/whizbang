namespace Whizbang.Core.Workers;

/// <summary>
/// Work a package adds to the periodic maintenance cycle.
/// </summary>
/// <remarks>
/// <para>
/// A step runs after the built-in housekeeping, inside the same cycle: only once the schema is ready,
/// only when the service has settled (or the deferral limit forces the cycle), and on the cycle's
/// configured interval. That is the reason to be a step rather than a timer of one's own — work that
/// reads or repairs stored state belongs where the framework already decided it is safe to do so.
/// </para>
/// <para>
/// Steps run one after another, each in the cycle's service scope. A step that throws is logged and
/// the cycle moves on to the next, so one broken step never stops the others; cancellation is
/// shutdown and propagates.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/maintenance-steps#maintenance-steps</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerStepTests.cs</tests>
public interface IMaintenanceStep {
  /// <summary>A short name for logs.</summary>
  string Name { get; }

  /// <summary>Runs the step once.</summary>
  /// <param name="services">The maintenance cycle's scoped service provider.</param>
  /// <param name="cancellationToken">Cancels the step; set at shutdown.</param>
  Task RunAsync(IServiceProvider services, CancellationToken cancellationToken);
}
