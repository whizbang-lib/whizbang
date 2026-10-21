// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.SystemEvents.Security;
using Whizbang.Core.Tests.SystemEvents;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// A denied read records <see cref="AccessDenied"/> fire-and-forget (#1555). When that record cannot
/// be stored, the denial still reaches the caller as its own exception, and the lost record is logged
/// with its tenant and counted rather than vanishing.
/// </summary>
/// <docs>fundamentals/events/system-events#fire-and-forget-emits</docs>
/// <code-under-test>src/Whizbang.Core/Lenses/ScopedLensFactory.cs</code-under-test>
[Category("Core")]
[Category("Lenses")]
public class ScopedLensFactoryEmitFailureTests {
  [Test]
  public async Task ADeniedRead_WhoseRecordFailsToStore_StillDenies_AndTheLossIsLoggedAndCountedAsync() {
    var logger = new SystemEventFireAndForgetTests.CapturingLogger();
    var metrics = SystemEventFireAndForgetTests._newMetrics();
    var emitter = new SystemEventFireAndForgetTests.ThrowingEmitter(new InvalidOperationException("event store down"));
    var accessor = new ScopeContextAccessor {
      Current = new ScopeContext {
        Scope = new PerspectiveScope { TenantId = "tenant-denied" },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>()
      }
    };
    var services = new ServiceCollection();
    services.AddScoped<ScopedLensFactoryImplTests.ITestLensQuery, DeniedLensQuery>();
    var factory = new ScopedLensFactory(services.BuildServiceProvider(), accessor, new LensOptions(), emitter, logger, metrics);

    await Assert.That(() => factory.GetLens<ScopedLensFactoryImplTests.ITestLensQuery>(ScopeFilters.Tenant, Permission.Read("orders")))
      .Throws<AccessDeniedException>()
      .Because("the caller gets its denial whatever happens to the record of it");
    var entry = await logger.FirstEntry.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(entry.Message).Contains(nameof(AccessDenied));
    await Assert.That(entry.Message).Contains("tenant-denied");
    await Assert.That(SystemEventFireAndForgetTests._count(metrics, nameof(AccessDenied))).IsEqualTo(1L);
  }

  private sealed class DeniedLensQuery : ScopedLensFactoryImplTests.ITestLensQuery {
    public ScopeFilterInfo? AppliedFilter { get; private set; }
    public void ApplyFilter(ScopeFilterInfo filterInfo) => AppliedFilter = filterInfo;
  }
}
