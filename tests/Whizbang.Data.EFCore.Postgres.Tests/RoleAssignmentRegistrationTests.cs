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
/// Role assignment is opt-in for its first release (#966): <c>AddWhizbangRoleAssignment()</c>
/// replaces the duty elector with the role elector, keeps the session-lock elector as its delegate
/// for every duty it does not manage, and releases held roles when the host stops. Composed through
/// the real driver against the real container, in either registration order.
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
  public async Task AddWhizbangRoleAssignment_ReplacesTheElector_AndDelegatesTheMigratorAsync(
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
    await Assert.That(migrator.Grant!.Epoch).IsNull().Because("the migrator stays on the session-lock elector");
    await migrator.Grant.DisposeAsync();

    await Assert.That(provider.GetServices<IReleasesDutiesOnShutdown>().Single()).IsSameReferenceAs(elector);
    await Assert.That(provider.GetRequiredService<IRoleAssignmentReader>()).IsSameReferenceAs(elector);
    await Assert.That(provider.GetRequiredService<IPendingDutyWorkStore>()).IsTypeOf<PgPendingDutyWorkStore>();
    await Assert.That(services.Count(d => d.ServiceType == typeof(Whizbang.Core.Health.IWhizbangHealthSource)
        && d.ImplementationType == typeof(Whizbang.Core.Health.RoleAssignmentHealthSource))).IsEqualTo(1);
    var health = ActivatorUtilities.CreateInstance<Whizbang.Core.Health.RoleAssignmentHealthSource>(provider);
    await Assert.That((await health.ReportAsync(cancellationToken)).State).IsEqualTo(Whizbang.Core.Health.ComponentState.Operational)
      .Because("this instance holds the maintainer role right now");
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
  public async Task AddWhizbangRoleAssignment_RefusesANullCollectionAsync() {
    await Assert.That(() => RoleAssignmentServiceCollectionExtensions.AddWhizbangRoleAssignment(null!))
      .Throws<ArgumentNullException>();
  }
}
