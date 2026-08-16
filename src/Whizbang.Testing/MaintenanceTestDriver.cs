using Whizbang.Core.Workers;

namespace Whizbang.Testing;

/// <summary>
/// Test seam for driving one maintenance cycle synchronously. The worker's single-cycle method is
/// internal (production runs it on the timer loop); integration tests that need a real cycle
/// against a real database drive it through here instead of widening the production surface.
/// </summary>
/// <docs>proposals/pre-destruction-seam</docs>
public static class MaintenanceTestDriver {
  /// <summary>Runs exactly one maintenance cycle on <paramref name="worker"/>.</summary>
  /// <param name="worker">The maintenance worker under test.</param>
  /// <param name="cancellationToken">Cancels the cycle.</param>
  public static Task RunOnceAsync(MaintenanceWorker worker, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(worker);
    return worker.RunMaintenanceOnceAsync(cancellationToken);
  }
}
