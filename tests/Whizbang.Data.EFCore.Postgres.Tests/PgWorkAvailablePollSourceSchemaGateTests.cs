// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The work-available probes send nothing while the schema-ready gate is closed (#1636). Before it opens, the tables
/// they read may not exist, so on a fresh database every tick was a 42P01 on the server and a Warning in the log, and
/// nothing claims the work they would find before the gate opens anyway.
/// </summary>
/// <remarks>
/// Against a real, empty database: the gate closed, a tick returns quietly; the gate open, the same tick reaches the
/// server and fails on the missing table, which is the proof that the gate is the only thing that kept it from doing
/// so before.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgWorkAvailablePollSourceBase.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard5")]
public sealed class PgWorkAvailablePollSourceSchemaGateTests {
  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("pollgate");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  [Test]
  [Timeout(60000)]
  public async Task WhileTheSchemaGateIsClosed_ATickSendsNothing_AndOnceOpenItQueriesAsync(CancellationToken cancellationToken) {
    var gate = new SchemaReadyGate();
    var source = new PgPerspectiveWorkAvailablePollSource(
      new FakeTimeProvider(),
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = _database.ConnectionString }),
      new ConfigurationBuilder().Build(),
      new ServiceInstanceProvider((Guid)Whizbang.Core.ValueObjects.TrackedGuid.New(), "gate-svc", "gate-host", processId: 1),
      NullLogger<PgPerspectiveWorkAvailablePollSource>.Instance) {
      SchemaReadyGate = gate,
    };
    var sink = new CountingSink();
    await source.StartAsync(sink, cancellationToken);

    await source.TickForTestsAsync(cancellationToken);

    await Assert.That(sink.Received).IsEqualTo(0);
    gate.MarkReady();
    var failure = await Assert.That(async () => await source.TickForTestsAsync(cancellationToken))
      .Throws<PostgresException>()
      .Because("with the gate open the probe reaches the server, which has no work tables in this database");
    await Assert.That(failure!.SqlState).IsEqualTo("42P01");
  }

  private sealed class CountingSink : ISignalSink {
    public int Received { get; private set; }

    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      Received++;
      return ValueTask.CompletedTask;
    }
  }
}
