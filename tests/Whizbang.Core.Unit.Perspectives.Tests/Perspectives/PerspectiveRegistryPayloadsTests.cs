// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The perspective registry payloads each assembly registers at module load (#1548): what a driver that does not
/// generate its own payload reconciles into <c>wh_perspective_registry</c> at startup.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRegistryPayloads.cs</code-under-test>
[NotInParallel(nameof(PerspectiveRegistryPayloads))]
public class PerspectiveRegistryPayloadsTests {

  [Test]
  public async Task Register_ThenRegistered_ReturnsThePayloadUnderItsServiceAsync() {
    var service = $"service-{Guid.NewGuid():N}";

    PerspectiveRegistryPayloads.Register(service, "[{\"ClrTypeName\":\"A\"}]");

    await Assert.That(PerspectiveRegistryPayloads.Registered.Single(p => p.ServiceName == service).Json)
      .IsEqualTo("[{\"ClrTypeName\":\"A\"}]");
  }

  [Test]
  public async Task Register_SameServiceTwice_KeepsOnePayload_TheLatestAsync() {
    var service = $"service-{Guid.NewGuid():N}";

    PerspectiveRegistryPayloads.Register(service, "[1]");
    PerspectiveRegistryPayloads.Register(service, "[2]");

    var registered = PerspectiveRegistryPayloads.Registered.Where(p => p.ServiceName == service).ToList();
    await Assert.That(registered.Count).IsEqualTo(1)
      .Because("a module initializer that runs again re-registers the same assembly, which must not reconcile twice");
    await Assert.That(registered[0].Json).IsEqualTo("[2]");
  }

  [Test]
  public async Task Register_EmptyServiceOrPayload_ThrowsAsync() {
    await Assert.That(() => PerspectiveRegistryPayloads.Register("", "[]")).Throws<ArgumentException>();
    await Assert.That(() => PerspectiveRegistryPayloads.Register("service", "")).Throws<ArgumentException>();
  }
}
