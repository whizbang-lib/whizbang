// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Tests;

/// <summary>
/// Branch backfill for <see cref="ServiceRegistrationCallbacks.InvokeAll"/>: a raw-receptor
/// registration callback set by a generated module initializer is invoked with the host's
/// service collection.
/// </summary>
/// <remarks>
/// <c>[NotInParallel]</c> in the same group as every other mutator of the process-global
/// <see cref="ServiceRegistrationCallbacks"/> state; the callback is restored in a finally.
/// </remarks>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceRegistrationCallbacksBranchCoverageTests {

  private sealed class RawReceptorMarker;

  [Test]
  public async Task InvokeAll_RawReceptorsCallbackSet_IsInvokedWithTheServiceCollectionAsync() {
    var saved = ServiceRegistrationCallbacks.RawReceptors;
    try {
      ServiceRegistrationCallbacks.RawReceptors = static services => services.AddSingleton<RawReceptorMarker>();
      var services = new ServiceCollection();

      ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());

      await Assert.That(services.Any(d => d.ServiceType == typeof(RawReceptorMarker))).IsTrue()
        .Because("the raw-receptor callback is how a consumer assembly's discovered raw receptors reach the container");
    } finally {
      ServiceRegistrationCallbacks.RawReceptors = saved;
    }
  }
}
