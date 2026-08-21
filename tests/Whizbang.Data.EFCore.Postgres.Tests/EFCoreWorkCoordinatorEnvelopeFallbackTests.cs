using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Deep-path tests for the envelope rehydration performed by
/// <c>GetOrphanedLifecycleEventsAsync</c>: the JsonDocument.Parse fallbacks inside
/// <c>_deserializeEventEnvelope</c> (missing / incompatible JsonElement type info),
/// the per-row catch that skips undeserializable events with a warning, the
/// best-effort scope-to-hop restoration branches (user key, unrecognized keys,
/// unparseable scope), and the per-event-type query catch when a required table
/// is missing. Sabotage is injected through a wrapping IJsonTypeInfoResolver so
/// the real combined contexts keep serving every other type.
/// </summary>
/// <docs>fundamentals/events/event-store-serialization</docs>
[Category("Shard4")]
public class EFCoreWorkCoordinatorEnvelopeFallbackTests : EFCoreTestBase {
  private const string ORPHAN_EVENT_TYPE = "Whizbang.Tests.FallbackOrphanEvent";
  private static readonly string[] _onePerspective = ["P.One"];

  // --------------------------------------------------------------------------
  // Scope-to-hop restoration branches (healthy serializer)
  // --------------------------------------------------------------------------

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_UserOnlyScope_RestoresUserHopAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(dbContext, JsonContextRegistry.CreateCombinedOptions(), logger);

    var (streamId, eventId) = await _seedOrphanAsync(connection, scope: "{\"u\":\"user-9\"}");

    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      _orphanMap(), TimeSpan.FromHours(1));

    await Assert.That(orphans).Count().IsEqualTo(1);
    await Assert.That(orphans[0].EventId).IsEqualTo(eventId);
    await Assert.That(orphans[0].StreamId).IsEqualTo(streamId);

    // Scope short-key "u" (user) must be restored as a security-context hop even
    // when no tenant key is present.
    var envelope = (MessageEnvelope<JsonElement>)orphans[0].Envelope;
    await Assert.That(envelope.Hops).Count().IsEqualTo(1);
  }

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_ScopeWithoutTenantOrUser_YieldsNoHopsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(dbContext, JsonContextRegistry.CreateCombinedOptions(), logger);

    var (_, eventId) = await _seedOrphanAsync(connection, scope: "{\"other\":\"value\"}");

    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      _orphanMap(), TimeSpan.FromHours(1));

    await Assert.That(orphans).Count().IsEqualTo(1);
    await Assert.That(orphans[0].EventId).IsEqualTo(eventId);

    // Scope parsed fine but carried neither "t" nor "u" — no hop is synthesized.
    var envelope = (MessageEnvelope<JsonElement>)orphans[0].Envelope;
    await Assert.That(envelope.Hops).Count().IsEqualTo(0);
  }

  // --------------------------------------------------------------------------
  // _deserializeEventEnvelope fallbacks (sabotaged JsonElement resolution)
  // --------------------------------------------------------------------------

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_NoJsonElementTypeInfo_FallsBackToJsonDocumentParseAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(
      dbContext, _sabotagedOptions(SabotageMode.ReturnNull), logger);

    var (_, eventId) = await _seedOrphanAsync(
      connection, scope: "{\"t\":\"tenant-a\"}", eventData: "{\"answer\":7}");

    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      _orphanMap(), TimeSpan.FromHours(1));

    // GetTypeInfo throws NotSupportedException for JsonElement — the direct
    // JsonDocument.Parse fallback must still rehydrate the payload.
    await Assert.That(orphans).Count().IsEqualTo(1);
    await Assert.That(orphans[0].EventId).IsEqualTo(eventId);

    var envelope = (MessageEnvelope<JsonElement>)orphans[0].Envelope;
    await Assert.That(envelope.Payload.GetProperty("answer").GetInt32()).IsEqualTo(7);

    // Scope restoration also depends on JsonElement type info; its best-effort
    // catch swallows the failure and yields no hops instead of failing the row.
    await Assert.That(envelope.Hops).Count().IsEqualTo(0);
  }

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_IncompatibleJsonTypeInfo_FallsBackToJsonDocumentParseAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(
      dbContext, _sabotagedOptions(SabotageMode.ThrowIncompatible), logger);

    var (_, eventId) = await _seedOrphanAsync(connection, eventData: "{\"answer\":42}");

    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      _orphanMap(), TimeSpan.FromHours(1));

    // InvalidOperationException mentioning "incompatible JsonTypeInfo" takes the
    // second fallback branch — the row still deserializes via JsonDocument.Parse.
    await Assert.That(orphans).Count().IsEqualTo(1);
    await Assert.That(orphans[0].EventId).IsEqualTo(eventId);

    var envelope = (MessageEnvelope<JsonElement>)orphans[0].Envelope;
    await Assert.That(envelope.Payload.GetProperty("answer").GetInt32()).IsEqualTo(42);
  }

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_EnvelopeDeserializationThrows_SkipsRowAndLogsWarningAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(
      dbContext, _sabotagedOptions(SabotageMode.ThrowUnrelated), logger);

    var (_, eventId) = await _seedOrphanAsync(connection);

    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      _orphanMap(), TimeSpan.FromHours(1));

    // An InvalidOperationException without the incompatible-type marker escapes both
    // fallbacks; the per-row catch logs a warning and skips only that event.
    await Assert.That(orphans).Count().IsEqualTo(0);
    await Assert.That(logger.MessagesFor(LogLevel.Warning).Any(m => m.Contains("Failed to deserialize orphaned event", StringComparison.Ordinal))).IsTrue();

    // Reconciliation is read-only even on the failure path.
    var eventRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_event_store WHERE event_id = @id", ("id", eventId));
    await Assert.That(eventRemains).IsEqualTo(1L);
  }

  // --------------------------------------------------------------------------
  // Per-event-type query catch
  // --------------------------------------------------------------------------

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_CompletionsTableMissing_LogsWarningAndReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(dbContext, JsonContextRegistry.CreateCombinedOptions(), logger);

    await _seedOrphanAsync(connection);

    // Break the query precondition — the NOT EXISTS subquery target is gone.
    await using (var drop = connection.CreateCommand()) {
      drop.CommandText = "DROP TABLE wh_lifecycle_completions CASCADE";
      await drop.ExecuteNonQueryAsync();
    }

    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      _orphanMap(), TimeSpan.FromHours(1));

    await Assert.That(orphans).Count().IsEqualTo(0);
    await Assert.That(logger.MessagesFor(LogLevel.Warning).Any(m => m.Contains("Failed to query orphaned lifecycle events", StringComparison.Ordinal))).IsTrue();
  }

  // --------------------------------------------------------------------------
  // Helpers
  // --------------------------------------------------------------------------

  private static Dictionary<string, IReadOnlyList<string>> _orphanMap() {
    return new Dictionary<string, IReadOnlyList<string>> {
      [ORPHAN_EVENT_TYPE] = _onePerspective
    };
  }

  /// <summary>
  /// Seeds a fully-qualifying orphaned lifecycle event: an event-store row plus a
  /// caught-up cursor for the single expected perspective and no completion marker.
  /// </summary>
  private static async Task<(Guid StreamId, Guid EventId)> _seedOrphanAsync(
      NpgsqlConnection connection, string scope = "{}", string eventData = "{}") {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();

    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
        VALUES (@evt, @stream, @stream, 'agg', @type, @scope::jsonb, 1, NOW());
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@evt, @data::jsonb, '{}'::jsonb)";
      ins.Parameters.AddWithValue("evt", eventId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("type", ORPHAN_EVENT_TYPE);
      ins.Parameters.AddWithValue("data", eventData);
      ins.Parameters.AddWithValue("scope", scope);
      await ins.ExecuteNonQueryAsync();
    }

    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_cursors
          (stream_id, perspective_name, last_event_id, status, processed_at)
        VALUES (@stream, 'P.One', @last_event, 1, NOW())";
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("last_event", eventId);
      await ins.ExecuteNonQueryAsync();
    }

    return (streamId, eventId);
  }

  private static async Task<NpgsqlConnection> _openConnectionAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _createCoordinator(
      WorkCoordinationDbContext dbContext, JsonSerializerOptions jsonOptions, CapturingLogger logger) {
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, jsonOptions, logger);
  }

  private static async Task<long> _countAsync(
      NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// Builds serializer options whose resolver sabotages JsonElement resolution in the
  /// requested way while delegating every other type to the real combined contexts.
  /// </summary>
  private static JsonSerializerOptions _sabotagedOptions(SabotageMode mode) {
    var combined = JsonContextRegistry.CreateCombinedOptions();
    var inner = combined.TypeInfoResolver
      ?? throw new InvalidOperationException("Combined options must expose a TypeInfoResolver.");
    return new JsonSerializerOptions(combined) {
      TypeInfoResolver = new JsonElementSabotagingResolver(inner, mode)
    };
  }

  private enum SabotageMode {
    /// <summary>Resolver returns null → options.GetTypeInfo throws NotSupportedException.</summary>
    ReturnNull,
    /// <summary>Resolver throws InvalidOperationException mentioning "incompatible JsonTypeInfo".</summary>
    ThrowIncompatible,
    /// <summary>Resolver throws an InvalidOperationException neither fallback recognizes.</summary>
    ThrowUnrelated
  }

  private sealed class JsonElementSabotagingResolver : IJsonTypeInfoResolver {
    private readonly IJsonTypeInfoResolver _inner;
    private readonly SabotageMode _mode;

    public JsonElementSabotagingResolver(IJsonTypeInfoResolver inner, SabotageMode mode) {
      _inner = inner;
      _mode = mode;
    }

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
      if (type != typeof(JsonElement)) {
        return _inner.GetTypeInfo(type, options);
      }
      return _mode switch {
        SabotageMode.ReturnNull => null,
        SabotageMode.ThrowIncompatible => throw new InvalidOperationException(
          "Test resolver returned incompatible JsonTypeInfo for JsonElement."),
        _ => throw new InvalidOperationException("Test resolver refused JsonElement resolution.")
      };
    }
  }

  /// <summary>
  /// Level-agnostic logger that records every formatted message so warning-guarded
  /// catch branches stay live during the tests.
  /// </summary>
  private sealed class CapturingLogger : ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly Lock _lock = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
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
