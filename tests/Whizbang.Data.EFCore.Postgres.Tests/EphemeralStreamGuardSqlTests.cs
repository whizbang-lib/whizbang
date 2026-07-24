using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <c>GetStateBasedStreamIdsAsync</c> (E1 #13d) — the detection the rebuild/rewind
/// guards use to refuse <strong>StateBased</strong> streams. A stream holding any event flagged
/// <c>EventFlags.Ephemeral</c> (8) OR <c>EventFlags.Compacted</c> (16) — <c>(flags &amp; 24) &lt;&gt; 0</c> — is
/// StateBased (its state, not its log, is the source of truth); Sourced streams (flags 0) are not. The payoff
/// of the StateBased factoring is locked here: a compacted stream is guarded (StateBased) yet NOT reaped (the
/// reaper is keyed on self-destruct = flags&amp;8). Verified against a real Postgres.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralStreamGuardSqlTests : EFCoreTestBase {
  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _commitAsync(NpgsqlConnection connection, Guid streamId, string eventType, int flags) {
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}",
        "service_name": "test", "host_name": "test-host", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{Guid.NewGuid()}}", "Destination": "out-topic",
          "MessageType": "{{eventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{Guid.NewGuid()}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": {{flags}}
        }]
      }
      """;
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", request);
    _ = await call.ExecuteScalarAsync();
  }

  [Test]
  public async Task GetEphemeralStreamIds_ReturnsOnlyEphemeralStreamsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var ephemeralStream = Guid.NewGuid();
    var sourcedStream = Guid.NewGuid();
    var absentStream = Guid.NewGuid();  // never stored
    await _commitAsync(connection, ephemeralStream, "Whizbang.Tests.PresencePing", flags: 8);
    await _commitAsync(connection, sourcedStream, "Whizbang.Tests.OrderPlaced", flags: 0);

    var result = await coordinator.GetStateBasedStreamIdsAsync(new[] { ephemeralStream, sourcedStream, absentStream });

    await Assert.That(result).Contains(ephemeralStream).Because("A stream with an ephemeral event is ephemeral.");
    await Assert.That(result.Contains(sourcedStream)).IsFalse().Because("A Sourced stream is not ephemeral.");
    await Assert.That(result.Contains(absentStream)).IsFalse().Because("A stream with no stored events is not ephemeral.");
  }

  [Test]
  public async Task StateBased_IncludesCompacted_ButReaperDoesNotReapItAsync() {
    // The payoff of the StateBased factoring: a Compacted event (flags 16) is StateBased — permanent,
    // no-replay — so the rebuild/rewind guard refuses its stream, yet the reaper (self-destruct = flags&8)
    // never touches it. Guarded but not reaped.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var compactedStream = Guid.NewGuid();
    await _commitAsync(connection, compactedStream, "Whizbang.Tests.CompactedOrigin", flags: 16);

    var guarded = await coordinator.GetStateBasedStreamIdsAsync(new[] { compactedStream });
    await Assert.That(guarded).Contains(compactedStream)
      .Because("A compacted event is StateBased (flags&24), so the rebuild/rewind guard refuses its stream.");

    // Run the reaper; the compacted body must survive (the reaper is keyed on flags&8, not flags&16).
    await using (var m = connection.CreateCommand()) {
      m.CommandText = "SELECT * FROM perform_maintenance()";
      await using var r = await m.ExecuteReaderAsync();
      while (await r.ReadAsync()) { /* drain */ }
    }
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT count(*) FROM wh_event_body eb JOIN wh_event_store es ON es.event_id = eb.event_id WHERE es.stream_id = @sid";
      v.Parameters.AddWithValue("sid", compactedStream);
      await Assert.That((long)(await v.ExecuteScalarAsync())!).IsEqualTo(1L)
        .Because("Compacted is permanent by mode — the reaper (self-destruct = flags&8) never reaps it, so the authoritative origin survives.");
    }
  }

  [Test]
  public async Task GetEphemeralStreamIds_EmptyInput_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    _ = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var result = await coordinator.GetStateBasedStreamIdsAsync(Array.Empty<Guid>());
    await Assert.That(result.Count).IsEqualTo(0).Because("No candidates, nothing ephemeral.");
  }
}
