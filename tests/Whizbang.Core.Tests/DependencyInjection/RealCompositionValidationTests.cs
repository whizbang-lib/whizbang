using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DependencyInjection;
using CoreManifest = Whizbang.Core.DependencyInjection.WhizbangServiceRequirements;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// Reports what the generated manifest finds unsatisfied in a core-only composition.
/// </summary>
/// <remarks>
/// <para>
/// A core-only composition is deliberately incomplete: storage and transport drivers supply a
/// large part of the graph. So this does not assert emptiness, which would fail for correct
/// reasons. It records which services a composition without any driver is missing, which is the
/// list a host has to satisfy and the list worth checking against a deployed system.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
[Category("DependencyInjection")]
public class RealCompositionValidationTests {

  [Test]
  public async Task CoreOnlyCompositionReportsOnlyDriverSuppliedServicesAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbang();

    var missing = new List<string>();
    try {
      services.ValidateWhizbangRegistrations(CoreManifest.All);
    } catch (WhizbangRegistrationException ex) {
      foreach (var m in ex.Missing) {
        missing.Add($"{m.MissingService.Name} (needed by {m.NeededBy.Name})");
      }
    }

    missing.Sort(StringComparer.Ordinal);
    var distinct = missing.Distinct().ToList();

    // Everything left must be something a storage or transport driver supplies. This is the guard
    // against the false-positive classes coming back: optional dependencies that legitimately fall
    // back, container intrinsics that never appear as descriptors, and collection types that
    // resolve to empty. Each of those once accounted for dozens of entries here, and validation
    // runs at startup by default, so every one of them was an application that would fail to boot.
    var driverSupplied = new[] {
      "IEventStore", "IWorkCoordinator", "IWorkCoordinatorStrategy",
      "IPerspectiveSnapshotStore", "ITransport", "IWorkChannelWriter",
    };
    var unexpected = distinct
      .Where(m => !driverSupplied.Any(d => m.StartsWith(d, StringComparison.Ordinal)))
      .ToList();

    await Assert.That(unexpected).IsEmpty()
      .Because("a core-only composition should be missing only what a driver provides; anything "
             + "else is the validator reporting a gap that cannot be closed:\n  "
             + string.Join("\n  ", unexpected));

    // If this ever reports nothing at all, the likely cause is a manifest that stopped matching
    // registrations rather than a composition that became complete, and that failure is silent.
    await Assert.That(CoreManifest.All).IsNotEmpty();
  }
}
