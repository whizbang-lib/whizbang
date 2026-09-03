using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>A consumer that configures its DbContext with <c>UseNpgsql(NpgsqlDataSource)</c>, registers
/// nothing under <c>Whizbang:Database</c>, and has no <c>ConnectionStrings</c> section at all. Npgsql
/// redacts the password from every <c>ConnectionString</c> surface of a data-source-backed connection,
/// so the duty elector's connection-string fallback produced a valid string with no password and the
/// server refused it: "No password has been provided but the backend requires one (in
/// SASL/SCRAM-SHA-256)" — thrown inside the startup pipeline, surfaced as a Kestrel bind
/// cancellation three layers away (reported in the #619 thread).</para>
/// <para>The DbContext's own data source still holds the credentials. The driver hands it to the
/// notification stack, and the elector opens authenticated connections from it. This test composes
/// through the real driver against the real container (SCRAM-SHA-256, as Azure Postgres uses).</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/DbContextNotificationConnectionStringFallback.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class DutyElectionByoDataSourceE2ETests : EFCoreTestBase {
  private sealed class _pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "byo-duty-svc";
    public string HostName => "byo-duty-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  [Test]
  [Timeout(120000)]
  public async Task Elector_UnderUseNpgsqlDataSource_WithNoNotificationConfiguration_AcquiresTheDutyAsync(CancellationToken cancellationToken) {
    var pod = new _pod();
    await using (var ctx = CreateDbContext()) {
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
      await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), cancellationToken);
    }

    // The consumer's shape: its own credential-bearing data source, handed to EF Core directly and
    // registered nowhere else. Nothing under Whizbang:Database, no ConnectionStrings section.
    await using var applicationDataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddSingleton<IServiceInstanceProvider>(pod);
    services.AddDbContext<WorkCoordinationDbContext>(o => o.UseNpgsql(applicationDataSource));
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();

    var elector = provider.GetRequiredService<IDutyElector>();
    var attempt = await elector.TryAcquireAsync("migrator", cancellationToken);

    await Assert.That(attempt.Grant).IsNotNull()
      .Because("the only credential in this host lives inside the application's NpgsqlDataSource; "
             + $"a string round-tripped from the DbContext has no password. Refusal: {attempt.Refusal} — {attempt.Detail}");
    await attempt.Grant!.DisposeAsync();
  }
}
