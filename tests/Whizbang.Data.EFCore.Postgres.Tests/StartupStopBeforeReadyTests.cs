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
using Whizbang.Core.Transports;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A host stopped before its schema is ready ends quietly (#1991): stopped while it waits in Preflight on a fresh
/// database, it does not try to deregister an instance whose tables may not exist yet, and nothing it logs from the stop
/// on is a warning or an error.
/// </summary>
/// <remarks>
/// The host is held in Preflight by another session holding the schema lock, and the stop is issued on the bootstrap's
/// own signal that it found the lock held and is waiting for it, so the stop lands in Preflight every time.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/WhizbangShutdownService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/PerspectiveRunnerRegistryGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/SchemaReadyGatedService.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public sealed class StartupStopBeforeReadyTests {
  /// <summary>The schema lock for the default schema: the advisory key the bootstrap and the pass take.</summary>
  private static readonly long _schemaLock = SchemaInitializationLockKey.Compute("public");
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(120);

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("stopearly");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  [Test]
  [Timeout(300000)]
  public async Task AHostStoppedInPreflight_DoesNotDeregister_AndLogsNoWarningOrErrorAsync(CancellationToken cancellationToken) {
    await using var holder = new NpgsqlConnection(_database.ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({_schemaLock})", holder)) {
      await take.ExecuteNonQueryAsync(cancellationToken);
    }
    var log = new LevelLog();
    var contended = new Contended();
    var host = _buildHost(log, contended);
    try {
      // Not awaited: the host finishes starting only once startup is ready, which a held schema lock never lets it be.
      var starting = host.StartAsync(cancellationToken);
      await contended.Signal.WaitAsync(_deadline, cancellationToken);
      var beforeStop = log.Lines.Count;

      await host.StopAsync(cancellationToken);
      await starting.WaitAsync(_deadline, cancellationToken);

      await Assert.That(log.Lines).DoesNotContain(l => l.Contains("deregistering instance", StringComparison.Ordinal))
        .Because("an instance stopped before its schema is ready has nothing to deregister, and its tables may not exist");
      var loud = log.Lines.Skip(beforeStop).Where(l => l.StartsWith("Warning", StringComparison.Ordinal) || l.StartsWith("Error", StringComparison.Ordinal)
        || l.StartsWith("Critical", StringComparison.Ordinal)).ToList();
      await Assert.That(loud).IsEmpty()
        .Because("a stop during Preflight ends quietly; logged after the stop: " + string.Join(" | ", loud));
    } finally {
      // Released before the host is disposed: the bootstrap is still waiting on it, on a connection the host owns.
      await using (var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({_schemaLock})", holder)) {
        await release.ExecuteNonQueryAsync(CancellationToken.None);
      }
      host.Dispose();
    }
  }

  private IHost _buildHost(LevelLog log, Contended contended) {
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:db"] = _database.ConnectionString,
    });
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(log);
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Services.AddSingleton<ITransport>(new InProcessTransport());
    builder.Services.AddSingleton<ISchemaInitializationObserver>(contended);
    _ = builder.Services.AddWhizbang().WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    builder.Services.Replace(ServiceDescriptor.Singleton(new MessageSecurityOptions { AllowAnonymous = true }));
    return builder.Build();
  }

  /// <summary>Completes when schema initialization finds the schema lock held and is about to wait for it.</summary>
  private sealed class Contended : ISchemaInitializationObserver {
    private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Signal => _signal.Task;

    public ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) {
      _signal.TrySetResult();
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Every line the host logs at Information or above, prefixed with its level.</summary>
  private sealed class LevelLog : ILoggerProvider {
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];

    public List<string> Lines {
      get {
        lock (_lock) {
          return [.. _lines];
        }
      }
    }

    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    public void Dispose() { }

    private sealed class Writer(LevelLog log, string category) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        lock (log._lock) {
          log._lines.Add($"{logLevel} {category}: {formatter(state, exception)}");
        }
      }
    }
  }
}
