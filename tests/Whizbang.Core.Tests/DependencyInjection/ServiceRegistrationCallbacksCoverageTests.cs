using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// Coverage-round-23: the dispatcher registration snapshot/restore pair
/// (<c>ServiceRegistrationCallbacks.SnapshotDispatcherRegistrations</c> /
/// <c>ServiceRegistrationCallbacks.RestoreDispatcherRegistrations</c>) mirrors the
/// message-type-catalog snapshot/restore pair that <c>MultiServiceHarness</c> and
/// <c>TransportConsumerWorkerDiWiringTests</c> already lean on, but nothing yet exercises it
/// for the dispatcher list — these tests pin the round-trip behavior a future harness will need
/// when it isolates simulated services from each other's accumulated registrations.
/// </summary>
/// <remarks>
/// <c>[NotInParallel]</c>, same group as every other mutator of the process-global
/// <see cref="ServiceRegistrationCallbacks"/> state (see
/// <c>ServiceRegistrationCallbacksDispatcherUnionTests</c> and
/// <c>ServiceRegistrationCallbacksCatalogUnionTests</c>): interleaved with one of those,
/// this test's temporary clear/restore would land mid- another test's accumulation and both
/// would see the wrong registration set.
/// </remarks>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceRegistrationCallbacksCoverageTests {

  private sealed class PreSnapshotMarker;
  private sealed class PostSnapshotMarker;
  private sealed class LeakedMarker;

  // Targets ServiceRegistrationCallbacks.cs lines 95-97 and 99 (SnapshotDispatcherRegistrations)
  // and 102-107 (RestoreDispatcherRegistrations). If restore merged the saved set into whatever
  // had accumulated since the snapshot instead of replacing it, a harness that snapshots before
  // building one simulated service and restores after would leak that service's receptor
  // registrations into the next one — the next service's container would silently gain a
  // dispatcher entry (and the receptors behind it) that was never registered for it.
  [Test]
  public async Task SnapshotThenRestore_DiscardsRegistrationsAddedAfterTheSnapshotAsync() {
    var outerSaved = ServiceRegistrationCallbacks.SnapshotDispatcherRegistrations();
    try {
      ServiceRegistrationCallbacks.Dispatcher = null; // clear for an isolated simulation
      ServiceRegistrationCallbacks.Dispatcher = static services =>
        services.AddSingleton<PreSnapshotMarker>();

      var snapshot = ServiceRegistrationCallbacks.SnapshotDispatcherRegistrations();

      // Registered AFTER the snapshot was taken - must NOT survive a restore of that snapshot.
      ServiceRegistrationCallbacks.Dispatcher = static services =>
        services.AddSingleton<PostSnapshotMarker>();

      // Sanity: before restoring, both registrations are live (accumulation is working).
      var beforeRestore = new ServiceCollection();
      ServiceRegistrationCallbacks.InvokeAll(beforeRestore, new ServiceRegistrationOptions());
      await Assert.That(beforeRestore.Any(d => d.ServiceType == typeof(PreSnapshotMarker))).IsTrue();
      await Assert.That(beforeRestore.Any(d => d.ServiceType == typeof(PostSnapshotMarker))).IsTrue();

      ServiceRegistrationCallbacks.RestoreDispatcherRegistrations(snapshot);

      var afterRestore = new ServiceCollection();
      ServiceRegistrationCallbacks.InvokeAll(afterRestore, new ServiceRegistrationOptions());

      await Assert.That(afterRestore.Any(d => d.ServiceType == typeof(PreSnapshotMarker))).IsTrue()
        .Because("restore must bring back exactly what the snapshot captured");
      await Assert.That(afterRestore.Any(d => d.ServiceType == typeof(PostSnapshotMarker))).IsFalse()
        .Because("restore replaces the accumulated list with the snapshot - it must not merge, or " +
                 "a harness's snapshot/restore around one simulated service leaks that service's " +
                 "registrations into whatever runs next");
    } finally {
      ServiceRegistrationCallbacks.RestoreDispatcherRegistrations(outerSaved);
    }
  }

  // Targets the same ten lines from the opposite direction: a snapshot taken while the list was
  // EMPTY must, on restore, clear out registrations added afterward rather than leaving them in
  // place - the "no service built yet" baseline a harness snapshots before its first simulated
  // service must restore to truly empty, or the first service's receptors would keep firing for
  // every service built after it.
  [Test]
  public async Task RestoreWithEmptySnapshot_ClearsEveryAccumulatedRegistrationAsync() {
    var outerSaved = ServiceRegistrationCallbacks.SnapshotDispatcherRegistrations();
    try {
      ServiceRegistrationCallbacks.Dispatcher = null; // true empty baseline
      var emptySnapshot = ServiceRegistrationCallbacks.SnapshotDispatcherRegistrations();
      await Assert.That(emptySnapshot).IsEmpty();

      ServiceRegistrationCallbacks.Dispatcher = static services =>
        services.AddSingleton<LeakedMarker>();

      ServiceRegistrationCallbacks.RestoreDispatcherRegistrations(emptySnapshot);

      var services = new ServiceCollection();
      ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());

      await Assert.That(services.Any(d => d.ServiceType == typeof(LeakedMarker))).IsFalse()
        .Because("restoring an empty snapshot must clear the list, not leave post-snapshot " +
                 "additions in place");
    } finally {
      ServiceRegistrationCallbacks.RestoreDispatcherRegistrations(outerSaved);
    }
  }
}
