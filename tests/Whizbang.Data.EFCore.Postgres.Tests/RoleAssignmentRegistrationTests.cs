// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Role assignment is the Postgres driver's default (#966 phase 3): the driver registers the role
/// elector unless the host registered its own, keeps the session-lock elector as its delegate for
/// every duty it does not manage, holds the commit-order stamper's leadership as a role, binds the
/// options from configuration, and releases held roles when the host stops.
/// <c>AddWhizbangRoleAssignment()</c> tunes it in code, before or after the driver. Composed
/// through the real driver against the real container.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/RoleAssignmentServiceCollectionExtensions.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RoleAssignmentRegistrationTests : EFCoreTestBase {
  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "role-reg-svc";
    public string HostName => "role-reg-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class Step(string name, string capability) : IStartupStep {
    public StartupStepDescriptor Descriptor { get; } = new() { Name = name, RequiredCapability = capability };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) =>
      ValueTask.FromResult(new StartupStepReport(StartupStepOutcome.Completed));
  }

  private async Task<(ServiceCollection Services, ServiceProvider Provider)> _composeAsync(Pod pod, NpgsqlDataSource dataSource, bool roleAssignmentFirst, CancellationToken ct) {
    await using (var ctx = CreateDbContext()) {
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
      await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
    }
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddSingleton<IServiceInstanceProvider>(pod);
    services.AddSingleton<IStartupStep>(new Step("Rewrite", StartupDuties.MAINTAINER));
    services.AddSingleton<IStartupStep>(new Step("Everywhere", StartupCapabilities.EVERY_INSTANCE));
    if (roleAssignmentFirst) {
      services.AddWhizbangRoleAssignment(o => o.HoldLegacySessionLock = false);
    }
    services.AddDbContext<WorkCoordinationDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    if (!roleAssignmentFirst) {
      services.AddWhizbangRoleAssignment(o => o.HoldLegacySessionLock = false);
      services.AddWhizbangRoleAssignment();
    }
    return (services, services.BuildServiceProvider());
  }

  [Test]
  [Timeout(120000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AddWhizbangRoleAssignment_ReplacesTheElector_AndHoldsTheMigratorByAssignmentAsync(
      bool roleAssignmentFirst, CancellationToken cancellationToken) {
    var pod = new Pod();
    await using var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    var (services, provider) = await _composeAsync(pod, dataSource, roleAssignmentFirst, cancellationToken);
    await using var _ = provider;

    var elector = provider.GetRequiredService<IDutyElector>();
    await Assert.That(elector).IsTypeOf<PgRoleElector>();
    await Assert.That(provider.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value.HoldLegacySessionLock).IsFalse();

    var maintainer = await elector.TryAcquireAsync(StartupDuties.MAINTAINER, cancellationToken);
    await Assert.That(maintainer.Grant).IsNotNull()
      .Because($"the maintainer is held by assignment. Refusal: {maintainer.Refusal} — {maintainer.Detail}");
    await Assert.That(maintainer.Grant!.Epoch).IsEqualTo(1L);

    var migrator = await elector.TryAcquireAsync(StartupDuties.MIGRATOR, cancellationToken);
    await Assert.That(migrator.Grant).IsNotNull();
    await Assert.That(migrator.Grant!.Epoch).IsEqualTo(1L).Because("the migrator is held by assignment too (#966 phase 4)");
    await migrator.Grant.DisposeAsync();
    var stamper = await elector.TryAcquireAsync(Whizbang.Core.Notifications.CommitOrderStamperOptions.ROLE, cancellationToken);
    await Assert.That(stamper.Grant!.Epoch).IsEqualTo(1L).Because("the stamper's leadership is a role");
    var unmanaged = await elector.TryAcquireAsync("host-duty", cancellationToken);
    await Assert.That(unmanaged.Grant!.Epoch).IsNull().Because("a duty that is not a role stays on the session-lock elector");
    await unmanaged.Grant.DisposeAsync();

    await Assert.That(provider.GetServices<IReleasesDutiesOnShutdown>().Single()).IsSameReferenceAs(elector);
    await Assert.That(provider.GetRequiredService<IRoleAssignmentReader>()).IsSameReferenceAs(elector);
    await Assert.That(provider.GetRequiredService<IPendingDutyWorkStore>()).IsTypeOf<PgPendingDutyWorkStore>();
    // Registered through a factory (it reads through a deferred reader), so the descriptor names the type by
    // the factory's return type rather than ImplementationType.
    await Assert.That(services.Count(d => d.ServiceType == typeof(Whizbang.Core.Health.IWhizbangHealthSource)
        && d.ImplementationFactory?.Method.ReturnType == typeof(Whizbang.Core.Health.RoleAssignmentHealthSource))).IsEqualTo(1);
    var health = ActivatorUtilities.CreateInstance<Whizbang.Core.Health.RoleAssignmentHealthSource>(provider);
    await Assert.That((await health.ReportAsync(cancellationToken)).State).IsEqualTo(Whizbang.Core.Health.ComponentState.Operational)
      .Because("this instance holds the maintainer and stamper roles right now, and the migrator is idle between migrations");
    await stamper.Grant.DisposeAsync();
    var holder = provider.GetRequiredService<DutyHolderWorker>();
    await Assert.That(holder.Roles).IsEquivalentTo([StartupDuties.MAINTAINER])
      .Because("a duty-bound startup step becomes owed work for its role; an every-instance step does not");
    await Assert.That(services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null
        && d.Lifetime == ServiceLifetime.Singleton)).IsGreaterThan(0);
    await Assert.That(services.Count(d => d.ServiceType == typeof(IHostedService)
        && d.ImplementationType == typeof(DutyShutdownReleaseService))).IsEqualTo(1);
    var release = new DutyShutdownReleaseService(
      provider.GetServices<IReleasesDutiesOnShutdown>(), NullLogger<DutyShutdownReleaseService>.Instance);
    await release.StopAsync(cancellationToken);
    await Assert.That(await maintainer.Grant.VerifyStillHeldAsync(cancellationToken)).IsFalse()
      .Because("stopping the host released the role");
  }

  [Test]
  [Timeout(120000)]
  public async Task AddWhizbangRoleAssignment_RunsTheHolderLoopAsAHostedService_TheSameInstanceAsync(
      CancellationToken cancellationToken) {
    var pod = new Pod();
    await using var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    var (services, provider) = await _composeAsync(pod, dataSource, roleAssignmentFirst: true, cancellationToken);
    await using var owned = provider;

    var holder = provider.GetRequiredService<DutyHolderWorker>();
    // The hosted-service factories AddWhizbangRoleAssignment registered, invoked as the host would invoke them.
    // Resolving every hosted service instead would construct the rest of the fixture's workers, which it does not
    // wire up. A factory lambda is compiled into a closure type nested in the class that declares it.
    var extensions = typeof(RoleAssignmentServiceCollectionExtensions).FullName!;
    var hosted = services
      .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null
        && d.ImplementationFactory.Method.DeclaringType?.FullName?.StartsWith(extensions, StringComparison.Ordinal) == true)
      .Select(d => d.ImplementationFactory!(provider))
      .OfType<DutyHolderWorker>()
      .ToList();

    await Assert.That(hosted).Count().IsEqualTo(1)
      .Because("the host starts the holder loop once");
    await Assert.That(hosted[0]).IsSameReferenceAs(holder)
      .Because("the loop the host runs is the singleton the status and owed-work paths talk to, not a second copy");
  }

  [Test]
  public async Task AddWhizbangRoleAssignment_RefusesANullCollectionAsync() {
    await Assert.That(() => RoleAssignmentServiceCollectionExtensions.AddWhizbangRoleAssignment(null!))
      .Throws<ArgumentNullException>();
  }

  private sealed class HostElector : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromResult(DutyAttempt.Lost(DutyRefusal.Contended, "the host decides"));
  }

  private static ServiceCollection _driverOnly(Pod pod, NpgsqlDataSource dataSource, IDictionary<string, string?>? settings = null, Action<IServiceCollection>? before = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
    services.AddSingleton<IServiceInstanceProvider>(pod);
    before?.Invoke(services);
    services.AddDbContext<WorkCoordinationDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    return services;
  }

  [Test]
  [Timeout(120000)]
  public async Task TheDriver_HoldsDutiesByAssignment_WithoutAnExplicitCall_AndTheStamperIsARoleAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    await using var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    await using var provider = _driverOnly(pod, dataSource).BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IDutyElector>()).IsTypeOf<PgRoleElector>();
    var options = provider.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value;
    await Assert.That(options.Roles).Contains(Whizbang.Core.Notifications.CommitOrderStamperOptions.ROLE);
    await Assert.That(options.HoldLegacySessionLock).IsTrue().Because("the bridge is on by default in this release");
    var stamperKey = options.LegacyLockKeys[Whizbang.Core.Notifications.CommitOrderStamperOptions.ROLE];
    await Assert.That(stamperKey("svc")).IsEqualTo(Whizbang.Data.Postgres.CommitOrderStamperLockKey.Compute(
      "svc", new Whizbang.Core.Notifications.CommitOrderStamperOptions().AdvisoryLockKey))
      .Because("a bridged stamper holds, and a vote looks for, the lock an older stamper takes");
  }

  /// <summary>
  /// The framework's null elector registered as an instance is still a null default, not a host's own
  /// elector, so the driver replaces it with the role elector exactly as it replaces a type registration.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task TheDriver_ReplacesANullElectorRegisteredAsAnInstanceAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    await using var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    var services = _driverOnly(pod, dataSource, before: s => s.AddSingleton<IDutyElector>(NullDutyElector.Instance));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IDutyElector>()).IsTypeOf<PgRoleElector>();
  }

  [Test]
  [Timeout(120000)]
  public async Task TheDriver_LeavesAHostsOwnElector_AndRegistersNoRoleElectorAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    await using var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    var services = _driverOnly(pod, dataSource, before: s => s.AddSingleton<IDutyElector, HostElector>());
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IDutyElector>()).IsTypeOf<HostElector>();
    await Assert.That(services.Any(d => d.ServiceType == typeof(PgRoleElector))).IsFalse();
  }

  [Test]
  [Timeout(120000)]
  public async Task TheOptions_BindFromConfiguration_AndADisabledStamperIsNoRoleAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    await using var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    var settings = new Dictionary<string, string?> {
      ["Whizbang:Database:RoleAssignment:Enabled"] = "false",
      ["Whizbang:Database:RoleAssignment:HoldLegacySessionLock"] = "false",
      ["Whizbang:Database:RoleAssignment:RenewInterval"] = "00:00:02",
      ["Whizbang:Database:RoleAssignment:MissedRenewalsBeforeLapse"] = "4",
      ["Whizbang:Database:RoleAssignment:CooldownAfterLapse"] = "00:00:20",
      ["Whizbang:Database:RoleAssignment:OwedWorkRetryBase"] = "00:01:00",
      ["Whizbang:Database:Stamper:DisableStamper"] = "true",
    };
    await using var provider = _driverOnly(pod, dataSource, settings).BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value;
    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.HoldLegacySessionLock).IsFalse().Because("an operator can turn the bridge off early");
    await Assert.That(options.RenewInterval).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(options.MissedRenewalsBeforeLapse).IsEqualTo(4);
    await Assert.That(options.CooldownAfterLapse).IsEqualTo(TimeSpan.FromSeconds(20));
    await Assert.That(options.OwedWorkRetryBase).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(options.Roles).DoesNotContain(Whizbang.Core.Notifications.CommitOrderStamperOptions.ROLE)
      .Because("a disabled stamper has no leader to elect");
    var elector = (PgRoleElector)provider.GetRequiredService<IDutyElector>();
    await Assert.That(elector.Manages(StartupDuties.MAINTAINER)).IsFalse()
      .Because("disabled, every duty goes back to the session-lock elector");
    await Assert.That(provider.GetRequiredService<DutyHolderWorker>().Roles).IsEmpty();
  }
}
