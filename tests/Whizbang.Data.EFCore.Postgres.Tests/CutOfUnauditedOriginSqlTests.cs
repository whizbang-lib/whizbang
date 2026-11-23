// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2544: the cut gate (<c>_wh_cut_awaits_seal</c>) counts this service's seal for an origin only while this service
/// audits that origin: the origin is in <c>wh_integrity_known_origins</c> with a request address, which is what the audit
/// worker asks. A seal row for an origin whose checkpoints never reach this service has no path to advance, so a cut that
/// waited for it waited forever. Such an origin's received history is cut against its lane's sealed frontier only, and
/// <c>wh_integrity_unaudited_seals</c> names the origin so the missing checkpoint route is visible.
/// </summary>
/// <docs>resilience/stream-integrity#a-cut-waits-for-the-seal</docs>
[Category("Integration")]
[Category("Shard5")]
public class CutOfUnauditedOriginSqlTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.UnauditedCutEvent, Whizbang.Tests";

  [Test]
  public async Task Archive_OfAPositionedEventFromAnOriginThisServiceNeverAudits_IsNotHeldByItsSealAtZeroAsync() {
    await using var conn = await _openAsync();
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _receiveAsync(conn, origin, stream, [1, 2]);
    await _receiveAsync(conn, origin, (Guid)TrackedGuid.New(), [3, 4, 5, 6]);
    await _sealAsync(conn, width: 2);
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 0, NOW())", ("o", origin));
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");

    var status = await _archiveAsync(conn, stream);

    await Assert.That(status).IsEqualTo("archived")
      .Because("the origin is not a known origin, so nothing audits it and its seal at 0 can never advance");
    await Assert.That(await _countAsync(conn, stream)).IsEqualTo(0L);
  }

  [Test]
  public async Task Archive_OfAPositionedEventFromAnAuditedOrigin_IsStillHeldByItsSealAsync() {
    await using var conn = await _openAsync();
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _receiveAsync(conn, origin, stream, [1, 2]);
    await _receiveAsync(conn, origin, (Guid)TrackedGuid.New(), [3, 4, 5, 6]);
    await _sealAsync(conn, width: 2);
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 0, NOW())", ("o", origin));
    await _knowAsync(conn, origin, "origin.requests");
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");

    await Assert.That(await _archiveAsync(conn, stream)).IsEqualTo(ArchiveStatus.AWAITING_SEAL)
      .Because("this service audits the origin, so its seal advances once the audit verifies positions 1 and 2");
    await Assert.That(await _countAsync(conn, stream)).IsEqualTo(2L);
  }

  [Test]
  public async Task Archive_OfAPositionedEventFromAKnownOriginWithNoRequestAddress_IsNotHeldAsync() {
    await using var conn = await _openAsync();
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _receiveAsync(conn, origin, stream, [1, 2]);
    await _receiveAsync(conn, origin, (Guid)TrackedGuid.New(), [3, 4, 5, 6]);
    await _sealAsync(conn, width: 2);
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 0, NOW())", ("o", origin));
    await _knowAsync(conn, origin, null);
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");

    await Assert.That(await _archiveAsync(conn, stream)).IsEqualTo("archived")
      .Because("the audit asks an origin only at the request address it announced, so without one the seal cannot advance");
  }

  [Test]
  public async Task Archive_FromAnUnauditedOrigin_StillWaitsForItsLanesSealedFrontierAsync() {
    await using var conn = await _openAsync();
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _receiveAsync(conn, origin, stream, [1, 2, 3]);
    await _sealAsync(conn, width: 2);
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 0, NOW())", ("o", origin));
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");

    await Assert.That(await _archiveAsync(conn, stream)).IsEqualTo(ArchiveStatus.AWAITING_SEAL)
      .Because("position 3 is in the lane's open epoch: the digest still changes, whatever the seal");
  }

  [Test]
  public async Task UnauditedSeals_NameEachOriginWithASealThatNothingAudits_AndNotTheAuditedOnesAsync() {
    await using var conn = await _openAsync();
    var unaudited = (Guid)TrackedGuid.New();
    var noAddress = (Guid)TrackedGuid.New();
    var audited = (Guid)TrackedGuid.New();
    foreach (var origin in new[] { unaudited, noAddress, audited }) {
      await _executeAsync(conn,
        "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 7, NOW())", ("o", origin));
    }
    await _knowAsync(conn, noAddress, null);
    await _knowAsync(conn, audited, "origin.requests");

    var rows = new List<(Guid Origin, long SealedThrough)>();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT origin_service_id, sealed_through FROM wh_integrity_unaudited_seals() ORDER BY 1";
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        rows.Add((reader.GetGuid(0), reader.GetInt64(1)));
      }
    }

    await Assert.That(rows.ConvertAll(r => r.Origin)).IsEquivalentTo(new[] { unaudited, noAddress }.ToList());
    await Assert.That(rows.TrueForAll(r => r.SealedThrough == 7)).IsTrue();
  }

  [Test]
  public async Task Step_WarnsNamingEachUnauditedOrigin_AndGaugesHowManyThereAreAsync() {
    await using var conn = await _openAsync();
    var unaudited = (Guid)TrackedGuid.New();
    var audited = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 0, NOW())", ("o", unaudited));
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 5, NOW())", ("o", audited));
    await _knowAsync(conn, audited, "origin.requests");
    var noAddress = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 2, NOW())", ("o", noAddress));
    await _knowAsync(conn, noAddress, null);
    await using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var measured = new List<long>();
    using var listener = _listen(meters, measured);
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: meters.GetRequiredService<IMeterFactory>()));
    await using var services = new ServiceCollection().AddSingleton(_connection()).AddSingleton(metrics).BuildServiceProvider();
    var logger = new ListLogger();
    var step = new UnauditedSealReportStep(logger);

    var found = await step.ReportAsync(services, CancellationToken.None);

    await Assert.That(found.Select(s => s.OriginServiceId).ToList()).IsEquivalentTo(new[] { unaudited, noAddress }.ToList());
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning
        && e.Message.Contains(noAddress.ToString(), StringComparison.Ordinal)
        && e.Message.Contains("announce no request address", StringComparison.Ordinal))).IsEqualTo(1);
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning
        && e.Message.Contains(unaudited.ToString(), StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the origin with a seal nothing audits is named, so its missing checkpoint route can be found");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains(audited.ToString(), StringComparison.Ordinal))).IsFalse();
    listener.RecordObservableInstruments();
    await Assert.That(measured).IsEquivalentTo([2L]).Because("the gauge counts the seals nothing audits");
    await Assert.That(step.Name).IsEqualTo("unaudited-seal-report");

    await _knowAsync(conn, unaudited, "origin.requests");
    await _executeAsync(conn, "UPDATE wh_integrity_known_origins SET request_topic = 'origin.requests' WHERE origin_service_id = @o",
      ("o", noAddress));
    await step.RunAsync(services, CancellationToken.None);
    measured.Clear();
    listener.RecordObservableInstruments();
    await Assert.That(measured).IsEquivalentTo([0L]).Because("once its checkpoints arrive the origin is audited, and the gauge falls");
  }

  [Test]
  public async Task Step_FromServices_LogsToTheHostsLogger_AndRunsWithoutMetricsAsync() {
    await using var conn = await _openAsync();
    var unaudited = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 0, NOW())", ("o", unaudited));
    await using var services = new ServiceCollection().AddSingleton(_connection()).BuildServiceProvider();

    var found = await UnauditedSealReportStep.FromServices(services).ReportAsync(services, CancellationToken.None);

    await Assert.That(found.Any(s => s.OriginServiceId == unaudited && !s.Known)).IsTrue();
    await Assert.That(() => UnauditedSealReportStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new UnauditedSealReportStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new UnauditedSealReportStep(NullLogger.Instance).ReportAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheUnauditedSealReportStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<UnauditedSealReportStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1).Because("the report runs in every maintenance cycle, turnkey");
  }

  // ===== helpers =====

  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private static MeterListener _listen(ServiceProvider meters, List<long> measured) {
    var factory = meters.GetRequiredService<IMeterFactory>();
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Name == "whizbang.stream_integrity.unaudited_seals" && instrument.Meter.Scope == factory) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => measured.Add(value));
    listener.Start();
    return listener;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static Task _knowAsync(NpgsqlConnection conn, Guid origin, string? topic) =>
    _executeAsync(conn, """
      INSERT INTO wh_integrity_known_origins (origin_service_id, origin_service_name, request_topic, last_checkpoint_at)
      VALUES (@o, 'origin-svc', @t, NOW())
      """, ("o", origin), ("t", (object?)topic ?? DBNull.Value));

  /// <summary>Stores events received from an origin through the inbox, each at the given origin position.</summary>
  private static async Task _receiveAsync(NpgsqlConnection conn, Guid origin, Guid stream, long[] sequences) {
    Guid[] ids = [.. Enumerable.Range(0, sequences.Length).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var instance = (Guid)TrackedGuid.New();
    for (var i = 0; i < ids.Length; i++) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        SELECT * FROM store_inbox_messages(
          jsonb_build_array(jsonb_build_object(
            'MessageId', @id::text, 'HandlerName', 'CutHandler', 'MessageType', @type::text,
            'Envelope', jsonb_build_object('MessageId', @id::text, 'Payload', '{}'::jsonb, 'Hops', '[]'::jsonb),
            'EnvelopeType', 'env', 'StreamId', @sid::text, 'IsEvent', true, 'Flags', 0,
            'Metadata', '{}'::jsonb, 'SourceServiceId', @o::text, 'SourceCommitSequence', @seq)),
          @inst, NOW() + INTERVAL '5 minutes', NOW(), 100)
        """;
      cmd.Parameters.AddWithValue("id", ids[i]);
      cmd.Parameters.AddWithValue("type", TYPE);
      cmd.Parameters.AddWithValue("sid", stream);
      cmd.Parameters.AddWithValue("o", origin);
      cmd.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = sequences[i] });
      cmd.Parameters.AddWithValue("inst", (Guid)TrackedGuid.New());
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // Drained.
      }
    }
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instance), ("ids", ids));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW() - INTERVAL '1 hour', 10)",
      ("inst", instance));
    await _executeAsync(conn, "DELETE FROM wh_inbox_state WHERE message_id = ANY(@ids)", ("ids", ids));
    await _executeAsync(conn, "DELETE FROM wh_inbox WHERE message_id = ANY(@ids)", ("ids", ids));
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = ANY(@ids)", ("ids", ids));
    var stored = await _scalarAsync(conn,
      "SELECT COUNT(*) FROM wh_event_store WHERE event_id = ANY(@ids) AND origin_service_id = @o AND origin_commit_sequence IS NOT NULL",
      ("ids", ids), ("o", origin));
    if (stored != ids.Length) {
      throw new InvalidOperationException($"the setup stored {stored} of {ids.Length} positioned received events");
    }
  }

  private static async Task _sealAsync(NpgsqlConnection conn, int width) {
    await _setAsync(conn, "integrity_epoch_width", width.ToString(CultureInfo.InvariantCulture), "integer");
    await _executeAsync(conn, "UPDATE wh_stream_digests SET updated_at = NOW() - INTERVAL '2 hours'");
    await _executeAsync(conn, "SELECT close_digest_epochs(NOW(), 0, 1000)");
  }

  private static Task _setAsync(NpgsqlConnection conn, string key, string value, string type) =>
    _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES (@k, @v, @t, 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value), ("t", type));

  private static async Task<string> _archiveAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT archive_status FROM wh_archive_stream(@s, NULL, 'test://archive')";
    cmd.Parameters.AddWithValue("s", stream);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static Task<long> _countAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, "SELECT COUNT(*) FROM wh_event_store WHERE stream_id = @s", ("s", stream));

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // Drained: run for its effect, every row read so a failure surfaces.
    }
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }
}
