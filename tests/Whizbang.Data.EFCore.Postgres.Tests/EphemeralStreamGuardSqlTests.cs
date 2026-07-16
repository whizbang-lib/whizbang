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
/// Integration tests for <c>GetEphemeralStreamIdsAsync</c> (E1 #13d) — the detection the rebuild/rewind
/// guards use to refuse ephemeral streams. A stream holding any event with <c>EventFlags.Ephemeral</c>
/// (<c>(flags &amp; 8) = 8</c>) is ephemeral; Sourced streams are not. Verified against a real Postgres.
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

    var result = await coordinator.GetEphemeralStreamIdsAsync(new[] { ephemeralStream, sourcedStream, absentStream });

    await Assert.That(result).Contains(ephemeralStream).Because("A stream with an ephemeral event is ephemeral.");
    await Assert.That(result.Contains(sourcedStream)).IsFalse().Because("A Sourced stream is not ephemeral.");
    await Assert.That(result.Contains(absentStream)).IsFalse().Because("A stream with no stored events is not ephemeral.");
  }

  [Test]
  public async Task GetEphemeralStreamIds_EmptyInput_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    _ = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var result = await coordinator.GetEphemeralStreamIdsAsync(Array.Empty<Guid>());
    await Assert.That(result.Count).IsEqualTo(0).Because("No candidates, nothing ephemeral.");
  }
}
