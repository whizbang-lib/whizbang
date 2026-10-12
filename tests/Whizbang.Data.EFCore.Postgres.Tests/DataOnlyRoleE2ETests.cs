// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
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
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A service started the way a consumer starts it, with an application role that has data rights only and the
/// owner as the <c>-init</c> connection. Startup migrates, every startup and maintenance step runs, a published event
/// is applied to its perspective, and nothing on the way needs schema rights from the application role. The control start, with no <c>-init</c> connection,
/// fails: that is what shows the role is really restricted.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/SchemaInitConnection.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class DataOnlyRoleE2ETests {
  private const string PASSWORD = "data-only";

  private PerTestDatabase _database;
  private string _role = null!;
  private string _applicationConnection = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dataonly");
    _role = $"wh_app_{Guid.NewGuid():N}"[..30];
    var owner = new NpgsqlConnectionStringBuilder(_database.ConnectionString).Username;
    // Rights on what the owner creates later, granted up front: the role never gets them any other way.
    await _execAsync($"""
      CREATE ROLE {_role} LOGIN PASSWORD '{PASSWORD}';
      REVOKE CREATE ON SCHEMA public FROM PUBLIC;
      GRANT USAGE ON SCHEMA public TO {_role};
      ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {_role};
      ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {_role};
      ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO {_role};
      """);
    _applicationConnection = new NpgsqlConnectionStringBuilder(_database.ConnectionString) {
      Username = _role,
      Password = PASSWORD,
    }.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await using var drop = new NpgsqlCommand($"DROP ROLE IF EXISTS {_role}", admin);
    await drop.ExecuteNonQueryAsync();
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private IHost _buildHost(bool withInitConnection, ErrorLog errors) {
    // Production, as a deployed service runs, whatever DOTNET_ENVIRONMENT the machine running the test has: the
    // development container validation would trip over runners this test assembly generates for its other contexts.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
    var connectionStrings = new Dictionary<string, string?> { ["ConnectionStrings:db"] = _applicationConnection };
    if (withInitConnection) {
      connectionStrings["ConnectionStrings:db-init"] = _database.ConnectionString;
    }
    builder.Configuration.AddInMemoryCollection(connectionStrings);
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(errors);
    builder.Services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    builder.Services.Configure<MaintenanceWorkerOptions>(o => o.AllowTableRewrite = true);
    builder.Services.AddSingleton<ITransport>(new InProcessTransport());
    _ = builder.Services.AddWhizbang().WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    // The published event carries no caller: allowed, as a test host with no authentication allows it.
    builder.Services.Replace(ServiceDescriptor.Singleton(new MessageSecurityOptions { AllowAnonymous = true }));
    return builder.Build();
  }

  private static async Task<IReadOnlyList<StartupStepResult>> _startAsync(IHost host, CancellationToken cancellationToken) {
    await host.StartAsync(cancellationToken);
    await host.Services.GetRequiredService<IStartupReadySignal>().WaitForReadyAsync(cancellationToken);
    var state = host.Services.GetRequiredService<IStartupPipelineState>();
    await state.WaitForAsync(FrameworkStartupSteps.REWRITE, cancellationToken);
    return state.Completed;
  }

  [Test]
  [Timeout(180000)]
  public async Task UnderADataOnlyRole_WithTheOwnerAsInit_TheServiceStartsAndMaintainsItselfAsync(CancellationToken cancellationToken) {
    var errors = new ErrorLog();

    // First start: migrations run on the init connection and create everything the role is then granted.
    using (var first = _buildHost(withInitConnection: true, errors)) {
      var steps = await _startAsync(first, cancellationToken);
      await Assert.That(steps.Where(s => s.Outcome == StartupStepOutcome.Failed).Select(s => $"{s.Name}: {s.Reason}")).IsEmpty();
      await Assert.That(errors.Entries).IsEmpty()
        .Because("nothing a starting service does may need schema rights from its application role");
      await first.StopAsync(cancellationToken);
    }

    // A rewrite owed between starts, as a migration or the bloat detector records one.
    await TableBloat.ManufactureAsync(_database.ConnectionString, cancellationToken);
    await _execAsync("SELECT wh_request_table_rewrite('wh_settings')");

    // Second start: the rewrite step and every maintenance step run with the application role as the main connection.
    var restartErrors = new ErrorLog();
    using var second = _buildHost(withInitConnection: true, restartErrors);
    var restart = await _startAsync(second, cancellationToken);
    await Assert.That(restart.Where(s => s.Outcome == StartupStepOutcome.Failed).Select(s => $"{s.Name}: {s.Reason}")).IsEmpty();
    var rewrite = restart.Single(s => s.Name == FrameworkStartupSteps.REWRITE);
    await Assert.That(rewrite.Reason).Contains("rewrote 1 table")
      .Because($"the owed rewrite ran through the init connection; the step reported {rewrite.Outcome}: {rewrite.Reason}");

    await using (var scope = second.Services.CreateAsyncScope()) {
      foreach (var step in scope.ServiceProvider.GetServices<IMaintenanceStep>()) {
        await step.RunAsync(scope.ServiceProvider, cancellationToken);
      }
    }
    // A message through the running service: published, stored, claimed and applied to a perspective row.
    var perspectives = second.Services.GetServices<IHostedService>().OfType<PerspectiveWorker>().Single();
    var streamId = (Guid)TrackedGuid.New();
    var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    perspectives.OnPerspectiveEventProcessed += e => {
      if (e.StreamId == streamId) {
        applied.TrySetResult();
      }
    };
    await second.Services.GetRequiredService<IDispatcher>()
      .PublishAsync(new SampleOrderCreatedEvent { OrderId = new TestOrderId(streamId), Amount = 42m });
    await applied.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    await using (var db = new NpgsqlConnection(_database.ConnectionString)) {
      await db.OpenAsync(cancellationToken);
      await using var row = new NpgsqlCommand("SELECT count(*) FROM wh_per_order WHERE id = @id", db);
      row.Parameters.AddWithValue("id", streamId);
      await Assert.That((long)(await row.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(1L)
        .Because("the application role wrote the perspective row");
    }

    // Checked before stopping: a shutdown error says nothing about the role's rights.
    var runningErrors = restartErrors.Entries.ToArray();
    await second.StopAsync(cancellationToken);

    await Assert.That(runningErrors).IsEmpty()
      .Because("nothing a running service does may need schema rights from its application role");
  }

  [Test]
  [Timeout(180000)]
  public async Task UnderADataOnlyRole_WithNoInitConnection_StartupCannotMigrateAsync(CancellationToken cancellationToken) {
    using var host = _buildHost(withInitConnection: false, new ErrorLog());

    await Assert.That(async () => await host.StartAsync(cancellationToken)).Throws<PostgresException>()
      .Because("the role cannot create the schema: without this, the test above would prove nothing about the role");
  }

  /// <summary>Every log entry at Error or above, kept so a failure the service only logs still fails the test.</summary>
  private sealed class ErrorLog : ILoggerProvider {
    public ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        if (IsEnabled(logLevel)) {
          entries.Enqueue($"{category}: {formatter(state, exception)} {exception?.Message}");
        }
      }
    }
  }
}
