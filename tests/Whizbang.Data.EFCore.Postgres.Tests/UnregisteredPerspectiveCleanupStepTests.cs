// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
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
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2601: the maintenance step that removes what is left of a perspective no service runs any more: its cursors, its
/// queued rows and its snapshots. The shape a deployed environment held: a perspective removed from its service, about
/// 2,750 cursors left behind, two of them flagged to rewind with their triggers queued and leased on every pass, and no
/// association rows (the service's startup deleted them). A perspective name counts as no longer run only when this
/// service does not register it and no service's association rows name it, so a perspective a newer instance runs during
/// a rolling deploy (it registers its association rows at its startup) is never touched.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#perspectives-no-longer-registered</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/UnregisteredPerspectiveCleanupStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class UnregisteredPerspectiveCleanupStepTests : EFCoreTestBase {
  private const string RETIRED = "Probe.Chat.OzToolCompletion+Projection";
  private const string REGISTERED = "Probe.Chat.OzTool+Projection";
  private const string ELSEWHERE = "Probe.Other.Service+Projection";
  private const string COLLECTIVE = "__collective__";
  private const string EVENT_TYPE = "Probe.Contracts.ToolInvoked, Probe.Contracts";
  private const string REMOVED_METRIC = "whizbang.perspective.unregistered_cursors_removed";
  private const string CURSORS = "wh_perspective_cursors";
  private const string QUEUED_ROWS = "wh_perspective_events";
  private const string SNAPSHOTS = "wh_perspective_snapshots";

  [Test]
  public async Task Clean_RemovesTheCursorsRowsAndSnapshotsOfAPerspectiveNothingRuns_AndLeavesTheRestAsync() {
    await using var conn = await _openAsync();
    var streams = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      var stream = (Guid)TrackedGuid.New();
      streams.Add(stream);
      var eventId = await _seedEventAsync(conn, stream);
      foreach (var perspective in new[] { RETIRED, REGISTERED, ELSEWHERE, COLLECTIVE }) {
        await _seedCursorAsync(conn, stream, perspective, eventId, flagged: i == 0);
      }
    }
    await _associateAsync(conn, ELSEWHERE);
    var logger = new ListLogger();
    await using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var removed = new List<long>();
    using var listener = _listen(removed);
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory: meterServices.GetRequiredService<IMeterFactory>()));
    await using var services = _services(new UnregisteredPerspectiveCleanupOptions(), metrics);

    var result = await new UnregisteredPerspectiveCleanupStep(logger).CleanAsync(services, CancellationToken.None);

    await Assert.That((result.CursorsRemoved, result.RowsRemoved, result.SnapshotsRemoved)).IsEqualTo((3, 3, 3))
      .Because("each of the retired perspective's three cursors goes, with its queued row and its snapshot");
    await Assert.That(result.Perspectives).IsEquivalentTo([RETIRED]);
    await Assert.That(await _countAsync(conn, CURSORS, RETIRED)).IsEqualTo(0L);
    await Assert.That(await _countAsync(conn, QUEUED_ROWS, RETIRED)).IsEqualTo(0L);
    await Assert.That(await _countAsync(conn, SNAPSHOTS, RETIRED)).IsEqualTo(0L);
    await Assert.That(await _countAsync(conn, CURSORS, REGISTERED)).IsEqualTo(3L)
      .Because("this service registers it");
    await Assert.That(await _countAsync(conn, CURSORS, ELSEWHERE)).IsEqualTo(3L)
      .Because("another service's association rows name it: that service, or a newer instance of this one, runs it");
    await Assert.That(await _countAsync(conn, CURSORS, COLLECTIVE)).IsEqualTo(3L)
      .Because("the collective sink has no registration of its own");
    listener.RecordObservableInstruments();
    await Assert.That(removed.Sum()).IsEqualTo(3L);
    await Assert.That(logger.Entries.Count(e => e.Contains(RETIRED, StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the cycle names what it removed, once");
    var again = await new UnregisteredPerspectiveCleanupStep(logger).CleanAsync(services, CancellationToken.None);
    await Assert.That((again.Batches, again.CursorsRemoved, again.Status)).IsEqualTo((1, 0, "ok"));
    await Assert.That(logger.Entries.Count).IsEqualTo(1).Because("a cycle that removes nothing logs nothing");
  }

  [Test]
  public async Task Clean_TakesBatchesWithinTheCycleBudget_AndTheNextCycleTakesTheRestAsync() {
    await using var conn = await _openAsync();
    for (var i = 0; i < 3; i++) {
      var stream = (Guid)TrackedGuid.New();
      await _seedCursorAsync(conn, stream, RETIRED, await _seedEventAsync(conn, stream), flagged: false);
    }
    // No schema named: the step reaches the tables through the connection's search path.
    await using var services = _services(new UnregisteredPerspectiveCleanupOptions { BatchCursors = 1, MaxBatchesPerCycle = 2 }, schema: null);

    var first = await new UnregisteredPerspectiveCleanupStep(NullLogger.Instance).CleanAsync(services, CancellationToken.None);
    var second = await new UnregisteredPerspectiveCleanupStep(NullLogger.Instance).CleanAsync(services, CancellationToken.None);

    await Assert.That((first.Batches, first.CursorsRemoved, first.Status)).IsEqualTo((2, 2, "budget"))
      .Because("two batches of one cursor, then the cycle's budget is spent");
    await Assert.That((second.Batches, second.CursorsRemoved, second.Status)).IsEqualTo((2, 1, "ok"))
      .Because("the next cycle takes the rest, and stops at the first batch that finds nothing");
  }

  [Test]
  public async Task Clean_IsSkipped_WhenTurnedOff_OrWithoutAPerspectiveRegistryAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _seedCursorAsync(conn, stream, RETIRED, await _seedEventAsync(conn, stream), flagged: false);
    await using var off = _services(new UnregisteredPerspectiveCleanupOptions { Enabled = false });
    await using var noRegistry = _services(new UnregisteredPerspectiveCleanupOptions(), registry: false);

    var turnedOff = await new UnregisteredPerspectiveCleanupStep(NullLogger.Instance).CleanAsync(off, CancellationToken.None);
    var unregistered = await new UnregisteredPerspectiveCleanupStep(NullLogger.Instance).CleanAsync(noRegistry, CancellationToken.None);

    await Assert.That(turnedOff.Status).IsEqualTo("skipped (turned off)");
    await Assert.That(unregistered.Status).IsEqualTo("skipped (no perspective registry)")
      .Because("a service that registers no perspectives cannot say which ones it runs");
    await Assert.That(await _countAsync(conn, CURSORS, RETIRED)).IsEqualTo(1L);
  }

  [Test]
  public async Task FromServices_AndItsArguments_AreCheckedAsync() {
    await Assert.That(UnregisteredPerspectiveCleanupStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(new UnregisteredPerspectiveCleanupStep(NullLogger<UnregisteredPerspectiveCleanupStep>.Instance).Name)
      .IsEqualTo("unregistered-perspective-cleanup");
    await Assert.That(() => UnregisteredPerspectiveCleanupStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new UnregisteredPerspectiveCleanupStep((ILogger)null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new UnregisteredPerspectiveCleanupStep(NullLogger.Instance).CleanAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await using var services = new ServiceCollection().BuildServiceProvider();
    await new UnregisteredPerspectiveCleanupStep(NullLogger.Instance).RunAsync(services, CancellationToken.None);
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<UnregisteredPerspectiveCleanupStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1).Because("the cleanup runs in every Postgres host's maintenance cycle unless turned off");
  }

  // ===== Helpers =====

  private sealed class ListLogger : ILogger {
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Entries.Add(formatter(state, exception));
  }

  /// <summary>A registry that registers <see cref="REGISTERED"/> only.</summary>
  private sealed class OneRegistered : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(REGISTERED, $"global::{REGISTERED}", "global::Probe.Model", [EVENT_TYPE])];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private ServiceProvider _services(UnregisteredPerspectiveCleanupOptions cleanup, PerspectiveMetrics? metrics = null, bool registry = true,
      string? schema = "public") {
    var services = new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, schema))
      .AddSingleton(Options.Create(new PerspectiveWorkerOptions { UnregisteredPerspectiveCleanup = cleanup }));
    if (registry) {
      services.AddSingleton<IPerspectiveRunnerRegistry>(new OneRegistered());
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    return services.BuildServiceProvider();
  }

  private static MeterListener _listen(List<long> removed) {
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Name == REMOVED_METRIC) {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => removed.Add(value));
    listener.Start();
    return listener;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<Guid> _seedEventAsync(NpgsqlConnection conn, Guid streamId) {
    var id = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at,
                                  commit_sequence, flags)
      VALUES (@id, @sid, @sid, 'Probe', 1, @type, NULL, NOW(), nextval('wh_commit_seq'), 0);
      INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@id, '{}'::jsonb, '{}'::jsonb);
      """;
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("type", EVENT_TYPE);
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  /// <summary>A cursor at the event, flagged to rewind from it when asked, with its snapshot and a queued row of the event.</summary>
  private static async Task _seedCursorAsync(NpgsqlConnection conn, Guid streamId, string perspective, Guid eventId, bool flagged) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id)
      VALUES (@sid, @p, @e, @status, CASE WHEN @flagged THEN @e END);
      INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version)
      VALUES (@sid, @p, @e, jsonb_build_object(), 1, 1);
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @sid, @p, @e, 0, 1, 150, NOW());
      """;
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("p", perspective);
    cmd.Parameters.AddWithValue("e", eventId);
    cmd.Parameters.AddWithValue("status", flagged ? 34 : 2);
    cmd.Parameters.AddWithValue(nameof(flagged), flagged);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _associateAsync(NpgsqlConnection conn, string perspective) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @type, 'perspective', @p, 'other-service', @type, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """;
    cmd.Parameters.AddWithValue("type", EVENT_TYPE);
    cmd.Parameters.AddWithValue("p", perspective);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string table, string perspective) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE perspective_name = @p";
    cmd.Parameters.AddWithValue("p", perspective);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }
}
