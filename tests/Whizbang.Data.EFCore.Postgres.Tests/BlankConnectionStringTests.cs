// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Security;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A connection string key present with an empty value (an appsettings placeholder, or an environment variable set to
/// nothing) holds no connection string. Startup used to take an empty <c>-init</c> key as the schema connection and
/// fail to open it, rather than migrating on the main connection as it does when the key is absent; and an empty main
/// key got past the "not found" check, to fail later on a connection with no host.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class BlankConnectionStringTests {
  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("blankinit");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  private static IHost _buildHost(Dictionary<string, string?> connectionStrings) {
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
    builder.Configuration.AddInMemoryCollection(connectionStrings);
    builder.Logging.ClearProviders();
    builder.Services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    builder.Services.AddSingleton<ITransport>(new InProcessTransport());
    _ = builder.Services.AddWhizbang().WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    builder.Services.Replace(ServiceDescriptor.Singleton(new MessageSecurityOptions { AllowAnonymous = true }));
    return builder.Build();
  }

  [Test]
  [Timeout(180000)]
  [Arguments("")]
  [Arguments("   ")]
  public async Task ABlankInitConnectionString_MigratesOnTheMainConnectionAsync(string blank, CancellationToken cancellationToken) {
    using var host = _buildHost(new() {
      ["ConnectionStrings:db"] = _database.ConnectionString,
      ["ConnectionStrings:db-init"] = blank,
    });

    await host.StartAsync(cancellationToken);
    await host.Services.GetRequiredService<IStartupReadySignal>().WaitForReadyAsync(cancellationToken);
    var failed = host.Services.GetRequiredService<IStartupPipelineState>().Completed
      .Where(s => s.Outcome == StartupStepOutcome.Failed)
      .Select(s => $"{s.Name}: {s.Reason}");

    await Assert.That(failed).IsEmpty()
      .Because("a blank -init key is no -init connection, so schema initialization runs on the main connection");
    await host.StopAsync(cancellationToken);
  }

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task ABlankMainConnectionString_IsReportedAsNotFoundAsync(string blank) {
    using var host = _buildHost(new() { ["ConnectionStrings:db"] = blank });

    var resolve = () => host.Services.GetRequiredService<NpgsqlDataSource>();

    await Assert.That(resolve).Throws<InvalidOperationException>()
      .WithMessageContaining("Connection string 'db' not found");
  }
}
