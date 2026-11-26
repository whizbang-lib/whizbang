// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Parity guard (#2559): both Postgres drivers register the shared coordination alike: role assignment (the elector,
/// the duty holder loop, its owed-work store) and the elected partition assigner (its store, the claimers' cached
/// assignment, its subscription and its worker). The pieces are read from the shared registration itself, so a piece
/// added there is checked on both drivers without editing this test, and a driver that stops calling it fails here.
/// Composition only: nothing starts and nothing connects.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresCoordinationRegistration.cs</code-under-test>
[Category("Shard3")]
public class DriverCoordinationParityTests {
  private const string DAPPER_CONNECTION = "Host=localhost;Database=parity";
  private static readonly KeyValuePair<string, string>[] _noPerspectives = [];

  /// <summary>The EF Core driver's coordination, as its registration calls it (the Postgres notifications).</summary>
  private static ServiceCollection _efCore() {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();
    services.AddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    services.AddWhizbangPostgresNotifications();
    return services;
  }

  private static ServiceCollection _dapper() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangPostgres(DAPPER_CONNECTION, new JsonSerializerOptions(), initializeSchema: false, _noPerspectives);
    return services;
  }

  /// <summary>What a registration is, without building it: the service, the implementation when one is named, the lifetime.</summary>
  private static string _signature(ServiceDescriptor d) {
    var implementation = d.ImplementationType?.FullName
      ?? d.ImplementationInstance?.GetType().FullName
      ?? "factory";
    return $"{d.ServiceType.FullName} <- {implementation} ({d.Lifetime})";
  }

  /// <summary>
  /// The shared coordination's registrations, less the defaults it adds for a driver registered without the core (a
  /// null signal bus, a null connection mode), which a driver legitimately displaces with its own.
  /// </summary>
  private static List<string> _sharedPieces() => [.. new ServiceCollection().AddWhizbangPostgresCoordination()
    .Where(d => d.ImplementationInstance is not INullDefault)
    .Select(_signature)];

  private static List<string> _missingFrom(IServiceCollection driver) {
    // A multiset: each shared registration is matched against one of the driver's, so a piece the shared method adds
    // twice must be there twice.
    var remaining = driver.Select(_signature).ToList();
    return [.. _sharedPieces().Where(piece => !remaining.Remove(piece))];
  }

  [Test]
  public async Task EveryPieceOfTheSharedCoordination_IsRegisteredAlikeByBothDriversAsync() {
    await Assert.That(_sharedPieces().Count).IsGreaterThan(10)
      .Because("a positive control: the shared registration holds the elector, the duty loop and the assigner");
    await Assert.That(_missingFrom(_efCore())).IsEmpty().Because("the EF Core driver registers the shared coordination");
    await Assert.That(_missingFrom(_dapper())).IsEmpty().Because("the Dapper driver registers the shared coordination");
  }

  [Test]
  public async Task BothDrivers_ResolveTheSameElectorDutyLoopAndAssignerAsync() {
    await using var efCore = _efCore().BuildServiceProvider();
    await using var dapper = _dapper().BuildServiceProvider();

    foreach (var (driver, provider) in new (string, ServiceProvider)[] { ("EF Core", efCore), ("Dapper", dapper) }) {
      await Assert.That(provider.GetRequiredService<IDutyElector>()).IsSameReferenceAs(provider.GetRequiredService<PgRoleElector>())
        .Because($"{driver} elects its exclusive duties");
      await Assert.That(provider.GetRequiredService<IPartitionAssignmentSource>())
        .IsSameReferenceAs(provider.GetRequiredService<PartitionAssignmentCache>())
        .Because($"{driver}'s claims present the published assignment");
      var hosted = provider.GetServices<IHostedService>().ToList();
      await Assert.That(hosted.OfType<StartupStageGatedService<PartitionAssignerWorker>>().Count()).IsEqualTo(1)
        .Because($"{driver} runs the elected assigner");
      await Assert.That(hosted.OfType<StartupStageGatedService<DutyHolderWorker>>().Count()).IsEqualTo(1)
        .Because($"{driver} runs the duty holder loop");
      var roles = provider.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value;
      await Assert.That(roles.Manages(PartitionAssignerOptions.ROLE)).IsTrue();
      await Assert.That(roles.Manages(StartupDuties.MAINTAINER)).IsTrue();
    }
  }

  /// <summary>
  /// #2561: both drivers run the one shared migrator election (<c>MigratorElection</c>), which resolves the elector,
  /// this instance's identity and the migrator's lease from the host. Each driver's container answers all three alike,
  /// so an election is held on either driver: neither falls back to migrating every instance in turn for want of one.
  /// </summary>
  [Test]
  public async Task BothDrivers_ResolveWhatTheMigratorElectionNeedsAsync() {
    await using var efCore = _efCore().BuildServiceProvider();
    await using var dapper = _dapper().BuildServiceProvider();

    foreach (var (driver, provider) in new (string, ServiceProvider)[] { ("EF Core", efCore), ("Dapper", dapper) }) {
      await Assert.That(provider.GetService<IDutyElector>()).IsSameReferenceAs(provider.GetRequiredService<PgRoleElector>())
        .Because($"{driver} votes for the migrator duty through the shared elector");
      await Assert.That(provider.GetService<IServiceInstanceProvider>()).IsNotNull()
        .Because($"{driver} registers this instance before it votes");
      var roles = provider.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value;
      await Assert.That(roles.Manages(StartupDuties.MIGRATOR)).IsTrue()
        .Because($"{driver} holds the migrator duty as a role assignment, which a waiter watches");
      await Assert.That(roles.LeaseFor(StartupDuties.MIGRATOR))
        .IsEqualTo(new RoleAssignmentOptions().LeaseFor(StartupDuties.MIGRATOR))
        .Because($"{driver} holds the migrator to the same lease");
    }
  }
}
