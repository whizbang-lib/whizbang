// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// The generator-fed (dynamic) mode of <see cref="TrackedEventTypeRegistry"/> for an event type
/// no perspective tracks.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/TrackedEventTypeRegistry.cs</code-under-test>
/// <remarks>
/// Shares the "SyncTests" constraint with the other suites that touch the process-wide
/// registrations: they clear them, which would race the registration below.
/// </remarks>
[NotInParallel("SyncTests")]
public class TrackedEventTypeRegistryCoverageTests {
  private sealed record UntrackedEvent;

  /// <summary>
  /// An event no generated registration names has no perspective: the dispatcher must not start
  /// tracking it, so the lookup answers null rather than a name or an exception.
  /// </summary>
  [Test]
  public async Task GetPerspectiveName_DynamicMode_UntrackedEvent_ReturnsNullAsync() {
    var registry = new TrackedEventTypeRegistry();

    await Assert.That(registry.GetPerspectiveName(typeof(UntrackedEvent))).IsNull();
    await Assert.That(registry.ShouldTrack(typeof(UntrackedEvent))).IsFalse();
    await Assert.That(registry.GetPerspectiveNames(typeof(UntrackedEvent))).IsEmpty();
  }

  private sealed record GeneratorRegisteredEvent;

  /// <summary>
  /// An event the generated module initializer registered resolves to the perspective it named,
  /// read at call time from the process-wide registrations (the type is private to this test, so
  /// the registration is visible to no other test).
  /// </summary>
  [Test]
  public async Task GetPerspectiveName_DynamicMode_RegisteredEvent_ReturnsItsPerspectiveAsync() {
    SyncEventTypeRegistrations.Register(typeof(GeneratorRegisteredEvent), "CoverageProbePerspective");
    var registry = new TrackedEventTypeRegistry();

    await Assert.That(registry.GetPerspectiveName(typeof(GeneratorRegisteredEvent))).IsEqualTo("CoverageProbePerspective");
    await Assert.That(registry.ShouldTrack(typeof(GeneratorRegisteredEvent))).IsTrue();
  }
}
