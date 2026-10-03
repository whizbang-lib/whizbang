using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Pgvector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Chaos;

/// <summary>
/// Requirement 8 of #966 for real: the database server restarts while an instance holds a role.
/// Assignments are rows, not session locks, so the role survives; the holder's next renewal
/// presents the same epoch and is accepted, and the work owed before the restart runs exactly once
/// after it. In a server of its own, so restarting it disturbs no other test.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/184_RoleAssignmentResilience.sql</code-under-test>
[Category("Integration")]
[Category("Chaos")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RoleAssignmentDatabaseRestartChaosTests {
  [Before(Test)]
  public async Task SetupAsync() => await SharedPostgresContainer.InitializeAsync();

  [Test]
  [Timeout(300000)]
  public async Task DatabaseRestart_TheAssignmentSurvives_AndTheHolderKeepsItsEpochAsync(CancellationToken cancellationToken) {
    await using var server = await DedicatedPostgresServer.StartAsync(cancellationToken);
    await using var dataSource = _dataSource(server.ConnectionString);
    var options = new DbContextOptionsBuilder<WorkCoordinationDbContext>()
      .UseNpgsql(dataSource, npgsql => npgsql.UseWhizbangFunctions()).Options;
    await using (var init = new WorkCoordinationDbContext(options)) {
      await init.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
    }
    await ChaosPod.ExecuteAsync(server.ConnectionString, ChaosPod.EFFECTS_DDL, cancellationToken);
    // A lease long enough to outlast the restart itself: what is under test is that the row survives,
    // not how long the server takes to come back.
    var tuned = () => new RoleAssignmentOptions { RenewInterval = TimeSpan.FromSeconds(30) };
    var holder = await ChaosPod.JoinAsync(server.ConnectionString, () => new WorkCoordinationDbContext(options), cancellationToken);
    var other = await ChaosPod.JoinAsync(server.ConnectionString, () => new WorkCoordinationDbContext(options), cancellationToken);
    holder.Build(server.ConnectionString, server.ConnectionString, null, false, null, new FakeTimeProvider(), [], tuned());
    other.Build(server.ConnectionString, server.ConnectionString, null, false, null, new FakeTimeProvider(), [], tuned());
    await holder.PassAsync(cancellationToken);
    var epoch = await ChaosPod.EpochAsync(server.ConnectionString, StartupDuties.MAINTAINER, cancellationToken);
    await other.Store.OweAsync(RoleAssignmentChaosTests.ROLE, RoleAssignmentChaosTests.WORK, cancellationToken);

    await server.RestartAsync(cancellationToken);
    NpgsqlConnection.ClearAllPools();

    holder.Time.Advance(TimeSpan.FromSeconds(30));
    await holder.PassAsync(cancellationToken);
    await other.PassAsync(cancellationToken);

    await Assert.That(holder.Worker.Holds(StartupDuties.MAINTAINER)).IsTrue()
      .Because("the assignment is a row, so the restart did not take it away, and the renewal was accepted");
    await Assert.That(await ChaosPod.EpochAsync(server.ConnectionString, StartupDuties.MAINTAINER, cancellationToken)).IsEqualTo(epoch);
    await Assert.That(other.Worker.Holds(StartupDuties.MAINTAINER)).IsFalse();
    var effects = await ChaosPod.EffectsAsync(server.ConnectionString, cancellationToken);
    await Assert.That(effects.Count).IsEqualTo(1);
    await Assert.That(effects[0].Epoch).IsEqualTo(epoch!.Value);
  }

  private static NpgsqlDataSource _dataSource(string connectionString) {
    var builder = new NpgsqlDataSourceBuilder(connectionString);
    builder.ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions());
    builder.EnableDynamicJson();
    builder.UseVector();
    return builder.Build();
  }

  /// <summary>
  /// A PostgreSQL server in a container of its own, on a fixed host port so a restart keeps its
  /// address. Readiness is read from the server's own log ("ready to accept connections"), streamed
  /// as it is written, so neither starting nor restarting waits on a guess.
  /// </summary>
  private sealed class DedicatedPostgresServer : IAsyncDisposable {
    private const string IMAGE = "pgvector/pgvector:pg17";
    private const string USER = "chaos_user";
    private const string DATABASE = "chaos";
    private const string READY = "database system is ready to accept connections";
    private readonly string _name = $"whizbang-chaos-{Guid.NewGuid():N}";
    private readonly string _password = Guid.NewGuid().ToString("N");
    private int _port;

    public string ConnectionString => new NpgsqlConnectionStringBuilder {
      Host = "127.0.0.1",
      Port = _port,
      Username = USER,
      Password = _password,
      Database = DATABASE,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    }.ConnectionString;

    public static async Task<DedicatedPostgresServer> StartAsync(CancellationToken ct) {
      var server = new DedicatedPostgresServer();
      using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
        probe.Start();
        server._port = ((IPEndPoint)probe.LocalEndpoint).Port;
      }
      _ = await _dockerAsync(ct, "run", "--detach", "--name", server._name,
        "-e", $"POSTGRES_USER={USER}", "-e", $"POSTGRES_PASSWORD={server._password}", "-e", $"POSTGRES_DB={DATABASE}",
        "--publish", $"127.0.0.1:{server._port}:5432", IMAGE);
      // The image's first start initializes the data directory on a temporary, socket-only server and
      // then starts the real one: ready is the first "ready" line after the init completes.
      await server._waitForLogAsync(null, "PostgreSQL init process complete", ct);
      return server;
    }

    public async Task RestartAsync(CancellationToken ct) {
      _ = await _dockerAsync(ct, "restart", "--time", "5", _name);
      var started = (await _dockerAsync(ct, "inspect", "--format", "{{.State.StartedAt}}", _name)).Trim();
      await _waitForLogAsync(started, null, ct);
    }

    private async Task _waitForLogAsync(string? since, string? after, CancellationToken ct) {
      var args = since is null ? new[] { "logs", "--follow", _name } : ["logs", "--follow", "--since", since, _name];
      using var logs = Process.Start(_startInfo(args)) ?? throw new InvalidOperationException("docker logs did not start");
      var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var seenAfter = after is null;
      void read(string? line) {
        if (line is null) {
          return;
        }
        lock (ready) {
          if (!seenAfter && line.Contains(after!, StringComparison.Ordinal)) {
            seenAfter = true;
          } else if (seenAfter && line.Contains(READY, StringComparison.Ordinal)) {
            ready.TrySetResult();
          }
        }
      }
      logs.OutputDataReceived += (_, e) => read(e.Data);
      logs.ErrorDataReceived += (_, e) => read(e.Data);
      logs.BeginOutputReadLine();
      logs.BeginErrorReadLine();
      try {
        await ready.Task.WaitAsync(ct);
      } finally {
        logs.Kill();
      }
    }

    private static ProcessStartInfo _startInfo(IEnumerable<string> args) {
      var info = new ProcessStartInfo {
        FileName = DockerExecutable.PathOrThrow,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      foreach (var arg in args) {
        info.ArgumentList.Add(arg);
      }
      return info;
    }

    private static async Task<string> _dockerAsync(CancellationToken ct, params string[] args) {
      using var process = Process.Start(_startInfo(args)) ?? throw new InvalidOperationException("docker did not start");
      var output = await process.StandardOutput.ReadToEndAsync(ct);
      var error = await process.StandardError.ReadToEndAsync(ct);
      await process.WaitForExitAsync(ct);
      return process.ExitCode == 0 ? output : throw new InvalidOperationException($"docker {string.Join(' ', args)} failed: {error}");
    }

    public async ValueTask DisposeAsync() {
      NpgsqlConnection.ClearAllPools();
      try {
        _ = await _dockerAsync(CancellationToken.None, "rm", "--force", _name);
      } catch (InvalidOperationException) {
        // already gone
      }
    }
  }
}
