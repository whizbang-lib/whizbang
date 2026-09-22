using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the coordinator returns when a set-returning function it calls yields NO ROW AT ALL.
/// </summary>
/// <remarks>
/// <para>
/// This is a different fault from the one
/// <see cref="EFCoreWorkCoordinatorIntegrityLedgerDegradationTests"/> covers. There the function is
/// gone and Npgsql raises; here the function exists, the call succeeds, and the reader simply never
/// advances. Nothing throws, so nothing is logged and nothing degrades — the only thing standing
/// between that and an <see cref="InvalidOperationException"/> out of <c>reader.GetInt64(0)</c> on
/// an unpositioned reader is the guard on each read. An unpositioned-reader exception surfaces from
/// a maintenance or metrics tick as an opaque failure with no hint of which function went quiet.
/// </para>
/// <para>
/// The fault is produced by replacing the function body with <c>RETURN;</c>, keeping its declared
/// signature. That is truer than a mocked reader: the guard is exercised against the real Npgsql
/// reader in the real no-row state. <see cref="EFCoreTestBase"/> builds a fresh database per test,
/// so each crippled function is invisible to every other test.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class EFCoreWorkCoordinatorNoRowResultTests : EFCoreTestBase {

  private CapturingLogger _log = null!;

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _build(WorkCoordinationDbContext ctx) {
    _log = new CapturingLogger();
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, JsonContextRegistry.CreateCombinedOptions(), _log);
  }

  private async Task _executeAsync(string sql) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task ReclassifyEventsEphemeral_WhenTheFunctionYieldsNoRow_ReturnsTheEmptyResultAsync() {
    await using var ctx = CreateDbContext();

    // Same signature, no rows. CREATE OR REPLACE requires the declared shape to match.
    await _executeAsync("""
      CREATE OR REPLACE FUNCTION reclassify_events_ephemeral(p_event_types TEXT[])
      RETURNS TABLE(events_reclassified BIGINT, streams_reclassified BIGINT, streams_blocked BIGINT)
      AS $$ BEGIN RETURN; END; $$ LANGUAGE plpgsql;
      """);

    var result = await _build(ctx).ReclassifyEventsEphemeralAsync(["Some.Ephemeral.Event, Some.Asm"]);

    // With the function returning nothing there is no row to read, so the tallying return is
    // unreachable — reaching a value at all is only possible through the guard.
    await Assert.That(result).IsEqualTo(EphemeralReclassificationResult.Empty)
      .Because("a function that answers nothing reclassified nothing; the alternative is "
             + "GetInt64 on a reader that never advanced, which turns a quiet function into an "
             + "exception thrown from a maintenance tick");
  }

  [Test]
  public async Task RegisterTypeDefinition_WhenTheFunctionYieldsNoRow_ReturnsTheNoneSentinelAsync() {
    await using var ctx = CreateDbContext();

    await _executeAsync("""
      CREATE OR REPLACE FUNCTION register_type_definition(
        p_event_type TEXT, p_settings_hash BYTEA, p_schema_hash BYTEA, p_schema_version INTEGER)
      RETURNS TABLE(definition_id INTEGER, is_new BOOLEAN, previous_definition_id INTEGER)
      AS $$ BEGIN RETURN; END; $$ LANGUAGE plpgsql;
      """);

    var registration = await _build(ctx).RegisterTypeDefinitionAsync(
      "Some.Event, Some.Asm", settingsHashHex: "0a1b", schemaHashHex: "2c3d", schemaVersion: 4);

    await Assert.That(registration).IsEqualTo(TypeDefinitionRegistration.None)
      .Because("None is the documented reading for an engine whose fingerprint tables answer "
             + "nothing — and specifically IsNew false, because a true there would announce a "
             + "brand-new definition and drive lineage recording off a registration that never "
             + "happened");
    await Assert.That(registration.IsNew).IsFalse()
      .Because("the sentinel must never claim a definition was newly registered");
    await Assert.That(registration.PreviousDefinitionId).IsNull()
      .Because("there is no predecessor to link lineage from when nothing was registered");
  }

  [Test]
  public async Task GetIntegrityLedgerSummary_WhenTheSummaryYieldsNoRow_ReturnsEmptyWithoutReadingSealsAsync() {
    await using var ctx = CreateDbContext();

    // A seal row makes the two returns distinguishable: the healthy return carries Seals, the
    // no-row guard returns before the seals query is ever issued. Without this the assertion
    // would be satisfied by the normal path on an empty ledger as well.
    await _executeAsync(
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) "
      + "VALUES ('55555555-5555-5555-5555-555555555555', 4242, NOW())");

    await _executeAsync("""
      CREATE OR REPLACE FUNCTION wh_integrity_ledger_summary(p_max_attempts INTEGER)
      RETURNS TABLE(unhealed_buckets BIGINT, repair_exhausted BIGINT, oldest_unhealed_secs DOUBLE PRECISION)
      AS $$ BEGIN RETURN; END; $$ LANGUAGE plpgsql;
      """);

    var snapshot = await _build(ctx).GetIntegrityLedgerSummaryAsync(maxRepairAttempts: 5);

    await Assert.That(snapshot.Seals).IsEmpty()
      .Because("the guard returns before the seals query runs — a seeded seal coming back would "
             + "mean the reader was consumed past a row that does not exist");
    await Assert.That(snapshot).IsEqualTo(LedgerGaugeSnapshot.Empty)
      .Because("no row means nothing diverged as far as this pass can tell, which is the same "
             + "reading as a clean ledger and is what the gauges must publish");
    await Assert.That(_log.MessagesFor(LogLevel.Warning)).IsEmpty()
      .Because("this must be the guard and not the catch — the catch returns the same snapshot, "
             + "so without pinning the silence the test would pass on the failure path instead");
  }

  private sealed class CapturingLogger : ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly Lock _lock = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_lock) {
        _entries.Add((logLevel, formatter(state, exception)));
      }
    }

    public List<string> MessagesFor(LogLevel level) {
      lock (_lock) {
        return [.. _entries.Where(e => e.Level == level).Select(e => e.Message)];
      }
    }
  }
}
