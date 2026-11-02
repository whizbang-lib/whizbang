// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Security;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;
using Whizbang.Hosting.AspNet;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// While a long migration runs, Kubernetes sees the service as alive and not yet ready, from the start (#1636, owner
/// decision 2026-11-02): the heartbeat stays on the schema-ready gate only because of this. A real web host on a fresh
/// database is held in Migrate by another session holding the schema lock; while it is held, the liveness endpoint
/// answers Healthy (200), and the readiness endpoint answers at once with the stage named: Degraded under the Lenient
/// default, 503 under Strict. Once the lock is released and the instance reaches Running, readiness is Healthy.
/// </summary>
/// <remarks>
/// The lock is taken from inside the instance's own Elect stage (when it has won the migrator duty), so the pass that
/// follows finds it held and waits in Migrate; every wait is on the instance's own signal for the exact transition: the
/// pass saying it found the lock held, every hosted service having started (the server included), the stage reporter
/// saying Running.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/WhizbangDatabaseInitializerService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Hosting.AspNet/WhizbangManagedHealthCheck.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Health/WhizbangHealthAggregator.cs</code-under-test>
/// <docs>resilience/managed-resource-health</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public sealed class StartupHealthWhileMigratingTests {
  private static readonly long _schemaLock = SchemaInitializationLockKey.Compute("public");
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(120);

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("migrhealth");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  [Test]
  [Timeout(300000)]
  [Arguments(false, HttpStatusCode.OK, "Degraded")]
  [Arguments(true, HttpStatusCode.ServiceUnavailable, "Unhealthy")]
  public async Task HeldInMigrate_LivenessIsHealthy_AndReadinessSaysNotReadyWithTheStageAsync(
      bool strict, HttpStatusCode readyStatus, string readyState, CancellationToken cancellationToken) {
    await using var holder = new NpgsqlConnection(_database.ConnectionString);
    await holder.OpenAsync(cancellationToken);
    var hold = new MigrateHold(holder);
    await using var app = _buildApp(hold, strict);

    // Not awaited: the host finishes starting only once startup is ready (the ready service waits in StartedAsync). The
    // server is already serving by then, because it started with the hosted services, before any StartedAsync ran.
    var starting = app.StartAsync(cancellationToken);
    await hold.HeldInMigrate.WaitAsync(_deadline, cancellationToken);
    await hold.ServerStarted.WaitAsync(_deadline, cancellationToken);
    using var client = app.GetTestClient();

    var live = await client.GetAsync("/health/live", cancellationToken);
    var ready = await client.GetAsync("/health/ready", cancellationToken);
    var liveBody = await live.Content.ReadAsStringAsync(cancellationToken);
    var readyBody = await ready.Content.ReadAsStringAsync(cancellationToken);

    await Assert.That(app.Services.GetRequiredService<IStartupStageReporter>().Current!.Stage).IsEqualTo(StartupStage.Migrate)
      .Because("the probes were answered while the instance was still held in Migrate");
    await Assert.That(starting.IsCompleted).IsFalse()
      .Because("the host has not finished starting, and the probes are answered anyway");
    await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(liveBody).StartsWith("Healthy")
      .Because("an instance that is migrating is alive: Kubernetes must never restart it for it");
    await Assert.That(ready.StatusCode).IsEqualTo(readyStatus);
    await Assert.That(readyBody).StartsWith(readyState + " Migrate")
      .Because("readiness says the instance is not ready yet, and names the stage it is in");

    await using (var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({_schemaLock})", holder)) {
      await release.ExecuteNonQueryAsync(cancellationToken);
    }
    await hold.Running.WaitAsync(_deadline, cancellationToken);
    await starting.WaitAsync(_deadline, cancellationToken);
    var readyAtRunning = await client.GetAsync("/health/ready", cancellationToken);
    var readyAtRunningBody = await readyAtRunning.Content.ReadAsStringAsync(cancellationToken);
    await Assert.That(readyAtRunning.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(readyAtRunningBody);
    await Assert.That(readyAtRunningBody).StartsWith("Healthy Running");

    await app.StopAsync(cancellationToken);
  }

  private WebApplication _buildApp(MigrateHold hold, bool strict) {
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
    builder.WebHost.UseTestServer();
    builder.Configuration["ConnectionStrings:db"] = _database.ConnectionString;
    builder.Logging.ClearProviders();
    // First, so its StartedAsync runs first: every StartAsync, the server's included, has returned by then.
    builder.Services.AddSingleton<IHostedService>(hold);
    // No transport: this host only migrates and answers probes, and an unused transport reads as unreachable once Running.
    builder.Services.AddSingleton<IStartupStageObserver>(hold);
    builder.Services.AddSingleton<ISchemaInitializationObserver>(hold);
    if (strict) {
      builder.Services.AddWhizbangManagedHealth(o => o.Default = HealthPolicy.Strict);
    }
    _ = builder.Services.AddWhizbang().WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    builder.Services.AddWhizbangAspNet();
    builder.Services.Replace(ServiceDescriptor.Singleton(new MessageSecurityOptions { AllowAnonymous = true }));
    var app = builder.Build();
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live"), ResponseWriter = _write });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready"), ResponseWriter = _write });
    return app;
  }

  /// <summary>Writes the report's status and the startup stage name the readiness check carries.</summary>
  private static Task _write(HttpContext context, HealthReport report) {
    var stage = report.Entries.Values.Select(e => e.Data.TryGetValue("startup_stage_name", out var name) ? name as string : null)
      .FirstOrDefault(n => n is not null);
    context.Response.ContentType = "text/plain";
    var detail = string.Join("; ", report.Entries.Select(e => $"{e.Key}: {e.Value.Description}"));
    return context.Response.WriteAsync(
      (stage is null ? report.Status.ToString() : $"{report.Status} {stage}") + " | " + detail, Encoding.UTF8);
  }

  /// <summary>
  /// Takes the schema lock from another session once the instance has won the migrator duty, signals when the pass
  /// finds it held (in Migrate), and when the instance reaches Running.
  /// </summary>
  private sealed class MigrateHold(NpgsqlConnection holder)
      : IStartupStageObserver, ISchemaInitializationObserver, IHostedLifecycleService {
    private readonly TaskCompletionSource _serverStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _taken;

    public Task HeldInMigrate => _held.Task;
    public Task ServerStarted => _serverStarted.Task;

    public Task StartedAsync(CancellationToken cancellationToken) {
      _serverStarted.TrySetResult();
      return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task Running => _running.Task;

    public async ValueTask OnStageChangedAsync(StartupStageSnapshot snapshot, bool recorded, CancellationToken cancellationToken) {
      if (snapshot.Stage == StartupStage.Elect && snapshot.Message == "this instance migrates the schema"
          && Interlocked.Exchange(ref _taken, 1) == 0) {
        await using var take = new NpgsqlCommand($"SELECT pg_advisory_lock({_schemaLock})", holder);
        await take.ExecuteNonQueryAsync(cancellationToken);
      }
      if (snapshot.Stage == StartupStage.Running) {
        _running.TrySetResult();
      }
    }

    public ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) {
      if (Volatile.Read(ref _taken) == 1) {
        _held.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }
}
