// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The by-name type lookups run on every message: every lookup builds its key with
/// <see cref="EventTypeMatchingHelper.NormalizeTypeName"/>, and the name the framework stores is the wire form
/// ("Type, Assembly"), which has nothing to strip. Building that key must not copy the name, and a lookup by
/// a wire-form name must allocate nothing (#1756).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EventTypeMatchingHelper.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ControlPlaneTypeRegistry.cs</code-under-test>
[NotInParallel("AllocationMeasurement")]
public class TypeNameLookupAllocationTests {
  private const int ITERATIONS = 1_000;
  private const string WIRE_FORM = "Company.Contracts.Orders.Events.OrderPlaced, Company.Contracts.Orders";

  [Test]
  public async Task NormalizeTypeName_WireForm_ReturnsTheSameInstanceAsync() {
    var normalized = EventTypeMatchingHelper.NormalizeTypeName(WIRE_FORM);

    await Assert.That(ReferenceEquals(normalized, WIRE_FORM)).IsTrue()
      .Because("a name with no assembly metadata is already normalized; copying it is a wasted allocation per lookup");
  }

  [Test]
  public async Task NormalizeTypeName_WireForm_AllocatesNothingAsync() {
    _ = EventTypeMatchingHelper.NormalizeTypeName(WIRE_FORM);

    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < ITERATIONS; i++) {
      _ = EventTypeMatchingHelper.NormalizeTypeName(WIRE_FORM);
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(allocated).IsEqualTo(0L);
  }

  [Test]
  public async Task NormalizeTypeName_NameMentioningVersionOutsideMetadata_IsUnchangedAsync() {
    // "Version=" with no comma before it is not assembly metadata: the full scan runs and keeps every character
    const string name = "Company.Contracts.Version=Two, Company.Contracts";

    await Assert.That(EventTypeMatchingHelper.NormalizeTypeName(name)).IsEqualTo(name);
  }

  [Test]
  public async Task IsControlPlane_WireFormName_AllocatesNothingAsync() {
    var controlPlane = TypeNameFormatter.Format(typeof(IntegrityCheckpoint));
    _ = ControlPlaneTypeRegistry.IsControlPlane(controlPlane);
    _ = ControlPlaneTypeRegistry.IsControlPlane(WIRE_FORM);

    var before = GC.GetAllocatedBytesForCurrentThread();
    var hits = 0;
    for (var i = 0; i < ITERATIONS; i++) {
      hits += ControlPlaneTypeRegistry.IsControlPlane(controlPlane) ? 1 : 0;
      hits += ControlPlaneTypeRegistry.IsControlPlane(WIRE_FORM) ? 1 : 0;
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(hits).IsEqualTo(ITERATIONS)
      .Because("the framework checkpoint is control-plane and the domain event is not, on every call");
    await Assert.That(allocated).IsEqualTo(0L)
      .Because("the check runs on every received row");
  }
}
