// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// Registration and option-derived decisions that need no database: the pinned pool resolving a
/// named connection string in a container with no configuration, the role elector's "is a real
/// elector already registered" check against a null default registered as an instance, and the
/// duty elector's contention ceiling under a polling interval shorter than its floor.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresPinnedPoolServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/RoleAssignmentServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs</code-under-test>
[Category("Shard5")]
public class RegistrationAndOptionsBranchTests {
  private const string INLINE_CONNECTION_STRING = "Host=localhost;Username=pinned-user;Database=pinned";

  // A named connection string that cannot be looked up (no configuration is registered at all)
  // falls back to the inline one rather than disabling the pool or throwing.
  [Test]
  public async Task PinnedPool_NamedConnectionStringWithNoConfiguration_FallsBackToTheInlineOneAsync() {
    var services = new ServiceCollection();
    services.Configure<WhizbangPinnedPoolOptions>(o => {
      o.Enabled = true;
      o.ConnectionStringName = "PinnedPoolDb";
      o.ConnectionString = INLINE_CONNECTION_STRING;
    });
    services.AddSingleton(new PinnedWorkerRegistry());
    services.AddWhizbangPostgresPinnedPool();
    await using var provider = services.BuildServiceProvider();

    var pool = provider.GetRequiredService<IPinnedConnectionPool>();

    await Assert.That(pool).IsTypeOf<PinnedConnectionPool>();
    await Assert.That(((PinnedConnectionPool)pool).ConnectionStringForTesting).IsEqualTo(INLINE_CONNECTION_STRING);
  }

  // The null default elector is a placeholder that says "nothing is configured". Registered as an
  // instance it must not be mistaken for a real elector, or the role elector would never be added
  // and no duty could ever be held.
  [Test]
  public async Task RoleAssignmentByDefault_WithTheNullElectorRegisteredAsAnInstance_StillAddsTheRoleElectorAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IDutyElector>(NullDutyElector.Instance);

    services.AddWhizbangRoleAssignmentByDefault();

    await Assert.That(services.Any(d => d.ServiceType == typeof(PgRoleElector))).IsTrue();
  }

  [Test]
  public async Task DutyElector_PollingIntervalBelowTheFloor_UsesTheFloorAsTheCeilingAsync() {
    var elector = _elector(TimeSpan.FromMilliseconds(500));

    await Assert.That(elector.ContentionBackoffCeiling).IsEqualTo(PgDutyElector.ContentionBackoffFloor)
      .Because("the suppression window may never shrink below its own floor");
  }

  [Test]
  public async Task DutyElector_PollingIntervalAboveTheFloor_IsTheCeilingAsync() {
    var elector = _elector(TimeSpan.FromSeconds(45));

    await Assert.That(elector.ContentionBackoffCeiling).IsEqualTo(TimeSpan.FromSeconds(45));
  }

  private static PgDutyElector _elector(TimeSpan pollingFallbackInterval) => new(
    Options.Create(new WhizbangNotificationOptions { PollingFallbackInterval = pollingFallbackInterval }),
    new ConfigurationBuilder().Build(),
    new ServiceInstanceProvider(Guid.NewGuid(), "duty-svc", "duty-host", processId: 1),
    NullLogger<PgDutyElector>.Instance);
}
