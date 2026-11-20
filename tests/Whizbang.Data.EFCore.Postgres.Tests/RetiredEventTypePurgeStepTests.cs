// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The maintenance step that drives the purge of retired event types (#2329): batches of the configured size, at most the
/// configured number a cycle, nothing at all while no type is retired, and a name nothing is stored under reported once a
/// process. The Postgres driver registers it, so retiring a type takes nothing but its name.
/// </summary>
/// <docs>fundamentals/events/retired-event-types</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/RetiredEventTypePurgeStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class RetiredEventTypePurgeStepTests : EFCoreTestBase {
  private const string RETIRED = "Probe.Contracts.CouponIssued, Probe.Contracts";
  private const string KEEP = "Probe.Contracts.OrderPlaced, Probe.Contracts";

  private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task Purge_RunsBatchesWithinTheCycleBudget_AndTheNextCycleTakesTheRestAsync() {
    await using var conn = await _openAsync();
    for (var i = 0; i < 3; i++) {
      var stream = (Guid)TrackedGuid.New();
      await RetiredEventTypePurgeSqlTests.EmitAsync(conn, _t0.AddMinutes(i), (stream, RETIRED, 0, null, null), (stream, KEEP, 0, null, null));
    }
    await RetiredEventTypePurgeSqlTests.StampAsync(conn);
    var logger = new ListLogger();
    await using var services = _services(new RetiredEventTypesOptions { Names = { RETIRED }, BatchStreams = 1, MaxBatchesPerCycle = 2 });

    var first = await new RetiredEventTypePurgeStep(logger).PurgeAsync(services, CancellationToken.None);
    var second = await new RetiredEventTypePurgeStep(logger).PurgeAsync(services, CancellationToken.None);

    await Assert.That((first.Batches, first.EventsRetired, first.StreamsRetired, first.StreamsWaiting, first.Status))
      .IsEqualTo((2, 2L, 2, 0, "ok")).Because("two batches of one stream, then the cycle's budget is spent");
    await Assert.That((second.Batches, second.EventsRetired, second.StreamsRetired))
      .IsEqualTo((2, 1L, 1)).Because("the next cycle takes the rest, and stops at the first batch that finds nothing");
    await Assert.That(await _countAsync(conn, RETIRED)).IsEqualTo(0L);
    await Assert.That(await _countAsync(conn, KEEP)).IsEqualTo(3L);
    await Assert.That(logger.Entries.Count(e => e.Contains("removed", StringComparison.Ordinal))).IsEqualTo(2);
  }

  [Test]
  public async Task Purge_NothingRetired_TouchesNoDatabaseAsync() {
    // No connection is registered: a step that read the database would fail to resolve it.
    await using var services = new ServiceCollection().BuildServiceProvider();

    var result = await new RetiredEventTypePurgeStep(NullLogger.Instance).PurgeAsync(services, CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo("skipped (nothing retired)");
    await Assert.That(result.Batches).IsEqualTo(0);
  }

  [Test]
  public async Task Purge_ReportsANameNothingIsStoredUnder_OnceAProcess_AndTheWaitingStreamsAsync() {
    await using var conn = await _openAsync();
    var typo = $"Probe.Contracts.CuponIssued{(Guid)TrackedGuid.New():N}";
    var held = (Guid)TrackedGuid.New();
    var ids = await RetiredEventTypePurgeSqlTests.EmitAsync(conn, _t0, (held, RETIRED, 0, null, null));
    await RetiredEventTypePurgeSqlTests.StampAsync(conn);
    await _executeAsync(conn, "INSERT INTO wh_event_destruction_hold (event_id, hold_until) VALUES (@e, NOW() + INTERVAL '1 day')", ids[0]);
    var logger = new ListLogger();
    await using var services = _services(new RetiredEventTypesOptions { Names = { typo, RETIRED } });

    var first = await new RetiredEventTypePurgeStep(logger).PurgeAsync(services, CancellationToken.None);
    var second = await new RetiredEventTypePurgeStep(logger).PurgeAsync(services, CancellationToken.None);

    await Assert.That(first.UnknownNames).IsEquivalentTo([typo]);
    await Assert.That(second.UnknownNames).IsEmpty().Because("the report is a startup signal, made once a process");
    await Assert.That(logger.Entries.Count(e => e.Contains(typo, StringComparison.Ordinal) && e.Contains("misspelled", StringComparison.Ordinal)))
      .IsEqualTo(1);
    await Assert.That((first.Batches, first.StreamsWaiting, first.Status)).IsEqualTo((1, 1, "waiting (1 streams held)"));
    await Assert.That(logger.Entries.Count(e => e.Contains("1 streams wait", StringComparison.Ordinal))).IsEqualTo(2)
      .Because("a stream that keeps waiting is reported each cycle, so a hold that never lapses is seen");
  }

  [Test]
  public async Task FromServices_AndItsArguments_AreCheckedAsync() {
    await Assert.That(RetiredEventTypePurgeStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(new RetiredEventTypePurgeStep(NullLogger<RetiredEventTypePurgeStep>.Instance).Name).IsEqualTo("retired-event-type-purge");
    await Assert.That(() => RetiredEventTypePurgeStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new RetiredEventTypePurgeStep((ILogger)null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new RetiredEventTypePurgeStep(NullLogger.Instance).PurgeAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();

    await using var services = new ServiceCollection().AddSingleton(Options.Create(new RetiredEventTypesOptions())).BuildServiceProvider();
    await new RetiredEventTypePurgeStep(NullLogger.Instance).RunAsync(services, CancellationToken.None);
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<RetiredEventTypePurgeStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1)
      .Because("retiring a type takes only its name: the maintenance cycle purges it, idle-gated, under the maintainer turn");
  }

  // ===== Helpers =====

  private sealed class ListLogger : ILogger {
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add(formatter(state, exception));
  }

  private ServiceProvider _services(RetiredEventTypesOptions options) =>
    new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton(Options.Create(options))
      .BuildServiceProvider();

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string type) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_event_store WHERE event_type = @t";
    cmd.Parameters.AddWithValue("t", type);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("e", eventId);
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
